using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GrEmit;
using Lattice.Editor.Views;
using Lattice.IR;
using Lattice.Utils;
using Unity.Entities;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Lattice.Editor.Tools
{
    public static class MenuItems
    {
        /// <summary>Finds all graphs in the project and recompiles them. Useful for checking globally for compilation errors.</summary>
        [MenuItem("Lattice/Tools/Recompile All Graphs")]
        public static void RecompileAll()
        {
            var paths = AssetDatabase.FindAssets($"t:{nameof(LatticeGraph)}");
            List<LatticeGraph> graphs = new();
            foreach (var path in paths)
            {
                LatticeGraph graph =
                    AssetDatabase.LoadAssetAtPath<LatticeGraph>(AssetDatabase.GUIDToAssetPath(path));
                graphs.Add(graph);
            }

            Stopwatch timer = Stopwatch.StartNew();
            int nodes;
            try
            {
                var compilation = GraphCompiler.CompileStandalone(graphs, new GraphCompiler.Settings ()
                {
                    Debug = false
                });
                nodes = compilation.Graph.Nodes.Count;
            }
            catch (Exception)
            {
                Debug.LogError("ICE: Graph compilation threw fatal error:");
                throw;
            }
            timer.Stop();

            Debug.Log(
                $"(Lattice) Compiled all lattice graphs in project. ({timer.ElapsedMilliseconds}ms) [{graphs.Count} graphs] [{nodes} nodes]:\n" +
                string.Join("\n", graphs));
        }

        [MenuItem("Lattice/Tools/View GraphViz")]
        public static void ProjectWideGraphViz()
        {
            var paths = AssetDatabase.FindAssets($"t:{nameof(LatticeGraph)}");
            List<LatticeGraph> graphs = new();
            foreach (var path in paths)
            {
                LatticeGraph graph =
                    AssetDatabase.LoadAssetAtPath<LatticeGraph>(AssetDatabase.GUIDToAssetPath(path));
                graphs.Add(graph);
            }

            var compilation = GraphCompiler.CompileStandalone(graphs, settings: new GraphCompiler.Settings ()
            {
                Debug = false
            } );
            string dotString = GraphCompilation.ToDot(compilation);

            var p = FileUtil.GetUniqueTempPathInProject() + ".dot";
            File.WriteAllText(p, dotString);
            GraphUtils.OpenGraphviz(dotString);
        }

        [MenuItem("Lattice/Tools/Save Debug Assembly")]
        public static void SaveAssembly()
        {
            var paths = AssetDatabase.FindAssets($"t:{nameof(LatticeGraph)}");
            List<LatticeGraph> graphs = new();
            foreach (var path in paths)
            {
                graphs.Add(AssetDatabase.LoadAssetAtPath<LatticeGraph>(AssetDatabase.GUIDToAssetPath(path)));
            }

            var graph = GraphCompiler.CompileStandalone(graphs, new GraphCompiler.Settings() {
                Debug = true,
                AssemblyAccess = AssemblyBuilderAccess.RunAndSave
            });
            
            // Generate work units first.
            graph.GenerateWorkUnits();

            TypeBuilder typeBuilder = graph.CodeGenModule.DefineType("LatticeStaticFunctions",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract);

            foreach (var workUnit in graph.WorkUnitsTopological)
            {
                MethodBuilder method = typeBuilder.DefineMethod("LatticeWorkUnit_" + workUnit.Name,
                    MethodAttributes.Public | MethodAttributes.Static, CallingConventions.Any, typeof(void),
                    new[] { typeof(IRExecution), typeof(EntityManager) });

                GroboIL emit = new(method);

                if (!ILGeneration.EmitNodeExecutionIL(emit, graph, workUnit.Nodes))
                {
                    Debug.LogError("Cannot save assembly. IL generation failed.");
                    return;
                }

                Debug.Log(
                    $"(Lattice) Compiled work unit: [{workUnit.Name}] [{workUnit.Nodes.Count} nodes]:\n{string.Join("\n", workUnit.Nodes)}");
                Debug.Log(emit);
            }

            typeBuilder.CreateType();

            var assemblyFileName = graph.CodeGenAssembly.GetName().Name + ".dll";
            graph.CodeGenAssembly.Save(assemblyFileName);
            string destPath = Path.GetFullPath(Path.Combine("Library", "ScriptAssemblies", assemblyFileName));
            File.Delete(destPath);
            File.Move(assemblyFileName, destPath);
            Debug.Log($"Wrote to [{destPath}]");
        }

        /// <summary>If true, nodes will execute faster, but their values will not be visible in the Lattice Window.</summary>
        public const string EnableDebugMenu = "Lattice/Options/Enable Debug";

        [MenuItem(EnableDebugMenu)]
        private static void PerformAction()
        {
            EditorPrefs.SetBool("LATTICE_ENABLE_DEBUG", !EditorPrefs.GetBool("LATTICE_ENABLE_DEBUG"));
        }

        [MenuItem(EnableDebugMenu, true)]
        private static bool PerformActionValidation()
        {
            var isChecked = EditorPrefs.GetBool("LATTICE_ENABLE_DEBUG", false);
            Menu.SetChecked(EnableDebugMenu, isChecked);
            return true;
        }
    }
}
