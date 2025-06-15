using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Lattice.IR
{
    /// <summary>
    ///     Singleton state that holds the global executing graph for the current runtime. Graphs are added as entities
    ///     are spawned with lattice graphs on them.
    /// </summary>
    public class GlobalGraph
    {
        public static GlobalGraph Runtime = new("Runtime");
        public static GlobalGraph LanguageServer = new("Editor", forceDebug:true);
        
        // This stores the currently seen set of LatticeGraphs across all executors we've seen. This will slowly grow
        // to include all the graphs used in the game, recompiling whenever new graphs are seen.
        private HashSet<LatticeGraph> toplevelGraphs = new();

        // The current global graph for executing live entities.
        public GraphCompilation Graph;

        public delegate void GlobalGraphRecompileEvent(GraphCompilation compilation);

        public event GlobalGraphRecompileEvent OnGraphCompilation;

        // Forces debug compilation, which allows introspecting values of nodes.
        // Useful for tests especially. If false, will use the debug setting from preferences.
        public bool ForceDebug;
        public readonly string Name;

        public GlobalGraph(string name, bool forceDebug = false)
        {
            Name = name;
            ForceDebug = forceDebug;
        }

        /// <summary>Whether the global graph is compiling in debug mode.</summary>
        internal bool InDebug()
        {
            if (ForceDebug)
            {
                return true;
            }

#if UNITY_EDITOR
            return EditorPrefs.GetBool("LATTICE_ENABLE_DEBUG", false);
#else
            return false;
#endif
        }

        /// <summary>
        ///     Adds the graph to the global Lattice compilation unit, if it's not already in it. Recompiles the global graph
        ///     if it has changed.
        /// </summary>
        /// <seealso cref="Graph" />
        public void AddToCompilation(LatticeGraph graph)
        {
            foreach (LatticeGraph g in GraphCompiler.GetGraphDependencies(graph))
            {
                if (graph.IsMalformed())
                {
                    Debug.LogWarning($"(Lattice) Skipping compilation for graph with errors: [{graph}].");
                }

                if (toplevelGraphs.Add(g))
                {
                    // Debug.Log($"(Lattice) Adding LatticeGraph to compilation: [{g}]");
                }
            }
        }

        /// <summary>Gets the compiled global graph, making sure that the given graph is added and compiled.</summary>
        /// <param name="addGraph">Graph to add to the compilation.</param>
        public GraphCompilation AddAndRecompileIfNeeded(LatticeGraph addGraph)
        {
            AddToCompilation(addGraph);
            return RecompileIfNeeded();
        }

        public GraphCompilation RecompileIfNeeded(bool force = false)
        {
            GraphCompiler.Settings settings = new GraphCompiler.Settings
            {
                Debug = InDebug()
            };

            if (!force && Graph != null && Graph.TopLevelGraphs.SetEquals(toplevelGraphs) &&
                settings.Equals(Graph.Settings))
            {
                return Graph;
            }

            // Remove any deleted graphs.
            toplevelGraphs = toplevelGraphs.Where(g => g != null).ToHashSet();

            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                Graph = GraphCompiler.CompileFixedSet(toplevelGraphs, settings);
            }
            catch (Exception)
            {
                Debug.LogError("ICE: Graph compilation threw fatal error.");
                throw;
            }
            timer.Stop();

            Debug.Log(
                $"(Lattice) Recompiled {Name} Lattice Graph. {(settings.Debug ? "(debug)" : "(release)")} ({timer.ElapsedMilliseconds}ms) [{toplevelGraphs.Count} graphs]:\n" +
                string.Join("\n", toplevelGraphs));

            OnGraphCompilation?.Invoke(Graph);

            return Graph;
        }

        public void ClearCompilation()
        {
            toplevelGraphs.Clear();
            RecompileIfNeeded();
        }
    }
}
