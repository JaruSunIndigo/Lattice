using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using Lattice.Base;
using Lattice.IR;
using Unity.Assertions;
using UnityEngine;
using UnityEngine.Pool;

// todo: implement qualifiers

namespace Lattice.Nodes
{
    // References can either be rooted or relative:
    //  - Rooted: Path descends from a top-level graph.
    //  - Relative: Path descends from the graph this node is within.
    [Serializable]
    public abstract class CrossRefNode : LatticeNode
    {
        // If null, this is a relative reference.
        [CanBeNull]
        [SerializeField]
        protected LatticeGraph OtherGraph;

        /// <summary>
        /// A path of FileIds of each successive subgraph node in the node's path. The last node is the target node.
        /// </summary>
        [SerializeField]
        protected List<string> NodePath;

        [SerializeField]
        protected string OtherPort; // Must be non-null. "full value" of node is no longer a thing.

        private CodePath? resolved;

        public CodePath? ResolvedNode
        {
            get
            {
                // Resolve the node if it's null or no longer points to the correct node.
                if (resolved == null || NodePath.Count == 0 || resolved.Value.Last.FileId != NodePath.Last())
                {
                    if (OtherGraph == null || NodePath.Count == 0)
                    {
                        // Reference is not specified yet.
                        resolved = null;
                    }
                    else
                    {
                        using var _ = (CollectionPool<List<LatticeNode>, LatticeNode>.Get(out var nodes));

                        LatticeGraph graph = OtherGraph;
                        foreach (var id in NodePath)
                        {
                            LatticeNode nextNode = graph.nodes.Find(n => n.FileId == id) as LatticeNode;
                            if (nextNode == null)
                            {
                                // Cross reference failed to resolve.
                                return null;
                            }

                            nodes.Add(nextNode);
                            graph = nextNode.Graph;
                        }

                        resolved = new CodePath(OtherGraph, nodes);
                    }
                }

                return resolved;
            }
        }

        public string GetResolvedPath()
        {
            Assert.IsTrue(ResolvedNode.HasValue);

            StringBuilder b = new(ResolvedNode.Value.Root.name);

            foreach (var n in ResolvedNode)
            {
                b.Append('.');
                b.Append(n.Name);
            }

            if (!string.IsNullOrEmpty(OtherPort) && ResolvedNode.Value.Last.OutputPorts.Count > 1)
            {
                b.Append('.');
                b.Append(OtherPort);
            }

            return b.ToString();
        }

        /// <summary>
        ///     If the node has a set reference, but that target node could not be found. (As opposed to having no target node
        ///     set at all).
        /// </summary>
        public bool IsDisconnected()
        {
            return ResolvedNode == null && (OtherGraph != null || NodePath.Any());
        }

        public void SetTarget(CodePath path, string port)
        {
            OtherGraph = path.Root;
            OtherPort = port;
            resolved = null;
            
            NodePath = new List<string>();
            foreach (var n in path)
            {
                NodePath.Add(n.FileId);
            }

            UpdateAllPorts();
        }

        public override IEnumerable<LatticeGraph> GetDependencies()
        {
            if (OtherGraph != null)
            {
                yield return OtherGraph;
            }
        }
    }
}
