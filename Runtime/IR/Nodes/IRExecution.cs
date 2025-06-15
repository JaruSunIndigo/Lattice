using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using Lattice.Nodes;
using Lattice.Utils;
using Unity.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Pool;

[assembly: InternalsVisibleTo("Lattice.Tests")]

namespace Lattice.IR
{
    /// <summary>Debug information output by the execution during execution of the graph over a single frame.</summary>
    public class DebugData
    {
        /// <summary>
        ///     The output value of every node in the execution. Nodes store to this dictionary during execution so we can
        ///     render them in the editor window.
        /// </summary>
        public readonly ConcurrentDictionary<(Entity, IRNode), object> Values = new();

        // There's no concurrent hash set in .NET so we use a dictionary with a dummy value.
        /// <summary>The set of nodes we executed this frame. Used for verifying phases.</summary>
        public readonly ConcurrentDictionary<IRNode, byte> NodesRunThisFrame = new();

        /// <summary>
        ///     Whether a node should save its outputs to the debug database. We use this mask to control which nodes are
        ///     being 'debugged'. Saving all of this output data is slow (boxing), so it's best to only debug values when required
        ///     (ie. when the lattice window is open for a graph).
        /// </summary>
        // public NativeArray<bool> NodeDebugFlags;
    }

    /// <summary>
    ///     The top-level SharedContext class used for executing a graph. It holds all of the output values of nodes, and
    ///     provides functions for querying node values and properties.
    /// </summary>
    public class IRExecution
    {
        public static readonly bool Verbose = false;

        // todo: We don't *really* want to keep around a reference to the execution. When we move to full AOT compilation
        // we don't want to have to compile graphs at runtime. What we want instead is just the 
        // static data assets that the graph depends on. Like Nodes, Assets, etc. I suppose that's the same for now.
        /// <summary>The graph that we're executing.</summary>
        public readonly GraphCompilation Compilation;

        /// <summary>Data the editor uses to display information about the execution. Null when running in release mode.</summary>
        [CanBeNull]
        internal DebugData DebugData;

        // This state is set every execution:
        // Inputs to execution. Must be set before running:
        public readonly Dictionary<Qualifier, UnsafeList<Entity>> EntitiesByQualifier = new(new QualifierComparer());

        /// <summary>
        /// A mapping from entity to the index within that entity's lattice. Used for calculating random access for QualifierTransform.
        /// </summary>
        public readonly Dictionary<Qualifier, UnsafeHashMap<Entity, int>> EntityToLatticeIndex =
            new(new QualifierComparer());

        public readonly Dictionary<Entity, LatticeState> StateDict = new(new EntityComparer());

        // Scratch data used during execution. Must be concurrent because they are modified during execution.
        public readonly ConcurrentDictionary<IRNode, IList> NodeOutputLists = new();
        public readonly ConcurrentDictionary<IRNode, IList> NodeOutputExceptionLists = new();

        // Scratch buffers used to transfer node outputs/exceptions between phases. Either T or List<T>
        // Must be concurrent because they are modified during execution.
        public readonly ConcurrentDictionary<IRNode, object> CrossPhaseOutputLocals = new();
        public readonly ConcurrentDictionary<IRNode, object> CrossPhaseExceptionLocals = new();

        // Stores the job handle for each work unit. This must be in the context, because jobs in later phases
        // must be able to depend on job handles in earlier phases. Those jobs may still be running!
        public readonly Dictionary<WorkUnit, JobHandle> WorkUnitHandles = new();

        // Provides the EntityManager to nodes that execute on the main thread.
        public EntityManager EntityManager;

        // A profiler marker for each IRNode, indexed by IRNode idx.
        public ProfilerMarker[] ProfilerMarkers;

        private ProfilerMarker profilerExecuteGraph = new("Execute Lattice Graph");

        public IRExecution(GraphCompilation compilation)
        {
            Compilation = compilation;

            if (compilation.Settings.Debug)
            {
                DebugData = new DebugData();
            }

            if (compilation.ProfileMarkerOptions != ProfileMarkerGranularity.None)
            {
                ProfilerMarkers = new ProfilerMarker[compilation.Graph.Nodes.Count];
                for (int i = 0; i < compilation.Graph.Nodes.Count; i++)
                {
                    var node = compilation.Graph.Nodes[i];
                    var marker = compilation.ProfileMarkerOptions switch
                    {
                        ProfileMarkerGranularity.Graph => compilation.Graph.GetOwner(node)?.Last.Graph.name ?? "NoGraph",
                        ProfileMarkerGranularity.Node => compilation.Graph.GetOwner(node)?.GetDebugPath() ?? "NoNode",
                        ProfileMarkerGranularity.IrNode => node.ToString(),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    ProfilerMarkers[i] = new ProfilerMarker(ProfilerCategory.Scripts, marker, MarkerFlags.Script);
                }
            }
        }

        /// <summary>Wipes scratch data used during the frame for execution.</summary>
        public void ClearScratch()
        {
            // Clear the lists, but don't deallocate them, so we save on allocations.
            foreach (IList list in NodeOutputLists.Values)
            {
                list.Clear();
            }
            foreach (IList list in NodeOutputExceptionLists.Values)
            {
                list.Clear();
            }
        }

        public void ExecutePhase(EntityManager entityManager, Type phase)
        {
            Assert.IsTrue(typeof(ILatticePhaseSystem).IsAssignableFrom(phase),
                "Phase must extend from class LatticePhaseSystem.");

            if (Compilation.CannotBeExecuted)
            {
                return;
            }

            EntityManager = entityManager;

            // Loop through the jobs for the phase, and simply execute them one by one.
            try
            {
                // Topological sort order.
                List<WorkUnit> workUnits = Compilation.GetWorkUnitsForPhase(phase);

                if (workUnits == null)
                {
                    // No work units assigned to this phase.
                    return;
                }

                if (Compilation.CannotBeExecuted) {
                    // Generation of work units failed.
                    return;
                }

                NativeList<WorkUnitJob> jobs = new NativeList<WorkUnitJob>(Allocator.Temp);

                using (profilerExecuteGraph.Auto())
                {
                    foreach (WorkUnit w in workUnits)
                    {
                        ILGeneration.ExecuteGraph del = Compilation.GetDelegateForWorkUnit(w);

                        if (w.MustRunOnMainThread)
                        {
                            // Complete all dependencies (synchronization point).
                            foreach (var dep in w.Dependencies)
                            {
                                // Guaranteed to exist because work units are topologically sorted.
                                // So the dependencies should have already been scheduled!
                                if (!dep.MustRunOnMainThread)
                                {
                                    WorkUnitHandles[dep].Complete();
                                }
                            }

                            del(this, entityManager);
                        }
                        else
                        {
                            WorkUnitJob job = new()
                            {
                                Delegate = GCHandle.Alloc(del),
                                Context = GCHandle.Alloc(this),
                            };

                            // get all handles from dependencies
                            NativeList<JobHandle> deps = new NativeList<JobHandle>(Allocator.Temp);
                            foreach (var dep in w.Dependencies)
                            {
                                // Guaranteed to exist because work units are topologically sorted.
                                // So the dependencies should have already been scheduled!
                                if (!dep.MustRunOnMainThread)
                                {
                                    deps.Add(WorkUnitHandles[dep]);
                                }
                            }

                            JobHandle handle = job.Schedule(JobHandle.CombineDependencies(deps));
                            WorkUnitHandles[w] = handle;
                        }
                    }
                    
                    // We must complete all lingering jobs before returning. Usually this happens implicitly because 
                    // the final nodes are main thread ECS nodes, but sometimes we end up with extra nodes dangling 
                    // such as if we're running in debug mode.
                    // todo: We could let these continue to execute, but we'd need to store the job structs and do the
                    // free below at the end of the frame somewhere?.. or... use a weak reference? We need a guarantee
                    // it will be cleaned up once the job is done...
                    foreach(var job in WorkUnitHandles) {
                         job.Value.Complete();   
                    }

                    // With all the jobs complete, now we can free the hard references to the context objects. 
                    foreach (var job in jobs)
                    {
                        job.Delegate.Free();
                        job.Context.Free();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Lattice Phase execution failed. [{phase.FullName}] Exception:");
                Debug.LogException(e);
            }
        }

        public void SanityCheckOutputValues(IRNode node, object value)
        {
            // Check for NaNs to throw early errors on them.
            if ((value is float3 f3 && math.any(math.isnan(f3))) ||
                (value is float2 f2 && math.any(math.isnan(f2))) ||
                (value is float f && math.isnan(f)))
            {
                throw new Exception($"Node returned a NaN. [{value}].");
            }
        }

        /// <summary>Reduce qualifiers if the node does not require them.</summary>
        public Entity ReduceQualifiers(IRNode node, Entity qualifiers)
        {
            return Compilation.ReduceQualifiers(node, qualifiers);
        }

        /// <summary>The set of entities that have values for nodes in the given graph. Ie. "Which entities executed this graph?".</summary>
        /// <remarks>Organized by qualifier for each node.</remarks>
        public Dictionary<Qualifier, HashSet<Entity>> EntitiesInGraph(LatticeGraph graph)
        {
            // If we're asking about a subgraph, we'll need to specify which code path, or return all of them.
            Assert.IsTrue(Compilation.TopLevelGraphs.Contains(graph));

            Dictionary<Qualifier, HashSet<Entity>> result = new();

            HashSet<IRNode> nodesInGraph = Compilation.Graph.GetNodesInGraph(graph);

            Assert.IsNotNull(DebugData, "Graph was not run in debug mode.");
            foreach ((Entity entity, IRNode node) in DebugData!.Values.Keys)
            {
                if (nodesInGraph.Contains(node))
                {
                    Qualifier? q = Compilation.CompileNode(node).Qualifier;
                    if (q == null)
                    {
                        continue;
                    }

                    if (!result.ContainsKey(q.Value))
                    {
                        result.Add(q.Value, new HashSet<Entity>());
                    }
                    result[q.Value].Add(entity);
                }
            }

            return result;
        }

        public object GetDebugNodeValue(Entity qualifier, IRNode node)
        {
            if (DebugData == null)
            {
                throw new Exception($"(IRExecution) Executed Graph was not compiled in debug mode. [{node}]");
            }

            if (!DebugData.Values.TryGetValue((qualifier, node), out object value))
            {
                if (!Compilation.Graph.NodeIndices.ContainsKey(node))
                {
                    throw new Exception(
                        $"(IRExecution) No debug data available for node [{node}] at qualifier [{qualifier}]. The node was not a part of the graph. Was it pruned?");
                }

                if (DebugData.NodesRunThisFrame.ContainsKey(node))
                {
                    throw new Exception(
                        $"(IRExecution) No debug data saved for node [{node}] at qualifier [{qualifier}]. The node was not executed for this entity.");
                }
                
                throw new Exception(
                    $"(IRExecution) No debug data saved for node [{node}] at qualifier [{qualifier}], because the node was not executed.");
            }
            return value;
        }
    }

    /// <summary>
    ///     A specialized equality comparer for Entity, to avoid using the default ObjectEqualityComparer in a Dictionary,
    ///     which boxes structs by default.
    /// </summary>
    public class EntityComparer : IEqualityComparer<Entity>
    {
        public bool Equals(Entity x, Entity y)
        {
            return x.Equals(y);
        }

        public int GetHashCode(Entity obj)
        {
            return obj.GetHashCode();
        }
    }
}
