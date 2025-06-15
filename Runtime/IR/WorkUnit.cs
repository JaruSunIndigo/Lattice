using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Assertions;
using UnityEngine;
using UnityEngine.Pool;

namespace Lattice.IR
{
    public class WorkUnit
    {
        // this could be made a list for performance improvement
        public HashSet<IRNode> Nodes;

        public string Name; // Useful for debugging.

        /// <summary>
        /// The list of work units that must run before this one executes.
        /// </summary>
        public HashSet<WorkUnit> Dependencies = new();

        /// <summary>
        /// The list of work units that depend on this work unit.
        /// </summary>
        public HashSet<WorkUnit> Usages = new();

        /// <summary>
        /// If any nodes within this unit are locked to the main thread, the whole work unit must run on the main thread.
        /// </summary>
        public bool MustRunOnMainThread;

        public Type GetPhase(GraphCompilation c) => c.CompileNode(Nodes.First()).ExecutionPhase;

        /// <inheritdoc />
        public override string ToString()
        {
            return "WorkUnit:" + Name;
        }
    }

    public static class WorkUnitPartitioning
    {
        public static void PartitionGraphIntoWorkUnits(GraphCompilation compilation)
        {
            Assert.IsNull(compilation.WorkUnitsInPhase);
            Assert.IsNull(compilation.WorkUnitDelegates);
            Assert.IsNull(compilation.WorkUnitsTopological);
            Assert.IsNull(compilation.WorkUnitForNode);

            // 1. Put all nodes in solo groups.

            Dictionary<IRNode, WorkUnit> workUnitsForNode = new();

            int i = 0;
            foreach (var n in compilation.Graph.Nodes)
            {
                workUnitsForNode.Add(n,
                    new WorkUnit()
                        { Nodes = new HashSet<IRNode> { n }, Name = "WU" + i++ });
            }

            // 2. Merge all nodes that require being in the same group.
            foreach (var n in compilation.Graph.Nodes)
            {
                if (n is StateRefIRNode)
                {
                    // StateRef Nodes return a pointer, so they must run in the same work group as dependents.
                    foreach (var (_, usage) in n.Usages)
                    {
                        MergeWorkUnits(n, usage);
                    }
                }
            }

            // todo: We may need to merge stateref nodes that read from the same entity.. 
            // Hmm although maybe writing/reading from the LatticeState is fine because they will always read from
            // different slots.

            // todo: Actually.. we need to merge all nodes of the same phase.
            // 3. Merge all global nodes (these are quick to execute due to lack of vectorization)
            // IRNode first = null;
            // foreach (var n in compilation.Nodes)
            // {
            //     if (compilation.CompileNode(n).Qualifier == null)
            //     {
            //         if (first == null)
            //         {
            //             first = n;
            //             continue;
            //         }
            //
            //         MergeWorkUnits(compilation, first, n);
            //         workUnits[first].Name = "WorkUnitGlobal";
            //     }
            // }

            // Done. Work units all grouped up.

            using var __ = CollectionPool<HashSet<WorkUnit>, WorkUnit>.Get(out var uniqueWorkUnits);

            foreach (var unit in workUnitsForNode.Values)
            {
                uniqueWorkUnits.Add(unit);
            }

            // Build the graph of work unit dependencies.
            foreach (var unit in uniqueWorkUnits)
            {
                foreach (var node in unit.Nodes)
                {
                    foreach (var dep in node.InputNodes())
                    {
                        // Find the work units for node dependencies outside this unit.
                        WorkUnit depWorkUnit = workUnitsForNode[dep];
                        if (depWorkUnit != unit)
                        {
                            unit.Dependencies.Add(depWorkUnit);
                            depWorkUnit.Usages.Add(unit);
                        }
                    }

                    if (node.MustRunOnMainThread)
                    {
                        unit.MustRunOnMainThread = true;
                    }
                }
            }

            // Verify there's no cycles.
            // There are no cycles IFF it can be topologically sorted.
            List<WorkUnit> workUnitsTopological = TopologicalSort(uniqueWorkUnits);

            // Build list of work units for each phase
            compilation.WorkUnitsInPhase = new();
            foreach (var workUnit in workUnitsTopological)
            {
                Type phase = workUnit.GetPhase(compilation);
                if (!compilation.WorkUnitsInPhase.TryGetValue(phase, out List<WorkUnit> units))
                {
                    units = new();
                    compilation.WorkUnitsInPhase[phase] = units;
                }

                units.Add(workUnit);
            }

            // Verify all nodes in a work unit have the same phase.
            if (GraphCompiler.EnableVerboseChecks)
            {
                foreach (var unit in uniqueWorkUnits)
                {
                    Type firstPhase = null;
                    foreach (var n in unit.Nodes)
                    {
                        Type p = compilation.CompileNode(n).ExecutionPhase;
                        if (firstPhase == null)
                        {
                            firstPhase = p;
                            continue;
                        }
                        if (p != firstPhase)
                        {
                            throw new Exception(
                                $"Work Unit [{unit.Name}] had more than one execution phase: [{firstPhase}][{p}]");
                        }
                    }
                }
            }

            // Set the final output of partitioning.
            compilation.WorkUnitForNode = workUnitsForNode;
            compilation.WorkUnitsTopological = workUnitsTopological;

            return;

            void MergeWorkUnits(IRNode node1, IRNode node2)
            {
                var unit1 = workUnitsForNode[node1];
                var unit2 = workUnitsForNode[node2];

                if (unit1 == unit2)
                {
                    // Nodes are already in the same work unit.
                    return;
                }

                UnityEngine.Assertions.Assert.AreEqual(compilation.CompileNode(node1).ExecutionPhase,
                    compilation.CompileNode(node2).ExecutionPhase,
                    "Cannot merge two work units of nodes with different phases.");

                foreach (var n in unit1.Nodes)
                {
                    unit2.Nodes.Add(n);
                }

                foreach (var n in unit1.Nodes)
                {
                    workUnitsForNode[n] = unit2;
                }
            }
        }

        public static string ToDot(ICollection<WorkUnit> workUnits)
        {
            StringBuilder sb = new();
            sb.AppendLine("digraph WorkUnitGraph {");
            sb.AppendLine("  rankdir=TD;"); // Top-to-bottom layout (adjust as needed)

            // Define nodes
            foreach (var unit in workUnits)
            {
                sb.AppendLine($"  node_{unit.Name} [label=\"{EscapeDotLabel(unit.Name)}\"];");
            }

            // Define edges (dependencies)
            foreach (var unit in workUnits)
            {
                foreach (var dependency in unit.Dependencies)
                {
                    sb.AppendLine($"  node_{dependency.Name} -> node_{unit.Name};");
                }
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string EscapeDotLabel(string label)
        {
            return label.Replace("\"", "\\\"");
        }

        public static List<WorkUnit> TopologicalSort(ICollection<WorkUnit> workUnits)
        {
            // Topological sort for ordering.
            using var _ = CollectionPool<HashSet<WorkUnit>, WorkUnit>.Get(out var visited);
            using var __ = CollectionPool<HashSet<WorkUnit>, WorkUnit>.Get(out var workUnitSet);

            foreach (var n in workUnits)
            {
                workUnitSet.Add(n);
            }

            // The set of nodes in the graph whose dependencies have been fully added to the toposort.
            // Split between main thread and parallel work units, so we can sort parallel ones first.
            // We do this because main thread units block scheduling while they run.
            var nextMainThread = new Queue<WorkUnit>(
                workUnits.Where(n => n.Dependencies.Count(w => workUnitSet.Contains(w)) == 0 && n.MustRunOnMainThread));
            var nextParallel = new Queue<WorkUnit>(
                workUnits.Where(n =>
                    n.Dependencies.Count(w => workUnitSet.Contains(w)) == 0 && !n.MustRunOnMainThread));

            var sort = new List<WorkUnit>();

            // Try dequeing the parallel jobs first.
            while (nextParallel.TryDequeue(out WorkUnit workUnit) || nextMainThread.TryDequeue(out workUnit))
            {
                if (visited.Contains(workUnit))
                {
                    continue;
                }

                sort.Add(workUnit);
                visited.Add(workUnit);

                foreach (WorkUnit consumer in workUnit.Usages)
                {
                    if (consumer.Dependencies.All(n => visited.Contains(n)))
                    {
                        if (consumer.MustRunOnMainThread)
                        {
                            nextMainThread.Enqueue(consumer);
                        }
                        else
                        {
                            nextParallel.Enqueue(consumer);
                        }
                    }
                }
            }

            // This usually implies a cycle. If not all nodes were visited there were extra back-facing edges in the graph.
            // See Kahn's algorithm.
            Assert.AreEqual(workUnits.Count, visited.Count,
                "ICE: Topological sort failed. (didn't visit all work units). Cycle may exist.");
            Assert.AreEqual(sort.Count, workUnits.Count,
                "ICE: Topological sort failed. (didn't sort all work units)");

            return sort;
        }
    }
}
