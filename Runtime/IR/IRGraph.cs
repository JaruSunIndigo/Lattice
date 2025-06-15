using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using Lattice.Base;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Pool;
using Assert = Unity.Assertions.Assert;

namespace Lattice.IR
{
    public class IRGraph
    {
        /// <summary>The IR nodes associated with a specific LatticeNode.</summary>
        public class Mapping
        {
            // A list of all IRNodes created during the compilation of this LatticeNode.
            public readonly List<IRNode> Nodes = new();

            // Which IRNode will receive the toPort for the LatticeNode.
            // Some nodes may want to create a separate IRNode for every input as well, if several
            // IRNodes need access to it.
            // Note: Only accessible during graph construction, not during optimization.
            public Dictionary<string, (IRNode node, string irPort)?> InputPortMap = new();

            // A lookup table for getting the IRNode for a specific output port.
            public readonly Dictionary<string, IRNodeRef> OutputPortMap = new();

            /// <summary>
            ///     The 'primary' IR Node is the node that feeds the debug output of the original 'authoredNode'. Usually this
            ///     represents the 'value' of the authored node.
            /// </summary>
            public IRNodeRef PrimaryNode;

            /// <summary>Points at the output value of the state every frame. Used only for debugging.</summary>
            [CanBeNull]
            public IRNodeRef StateDebugNode;

            /// <summary>Incrementor that stores the next node id num. Used instead of Nodes.Length because nodes can be removed.</summary>
            public int NodeIdCount;
        }

        /// <summary>The raw list of all IRNodes in the compilation.</summary>
        public readonly List<IRNode> Nodes = new();

        /// <summary>Stores metadata for every LatticeNode in the compilation.</summary>
        private readonly Dictionary<CodePath, Mapping> Mappings = new();

        // Mapping of IRNode back to the LatticeNode owner.
        private readonly Dictionary<IRNode, CodePath> Parents = new();

        // The list of inputs nodes for this sub-graph. Input nodes must have a single input port.
        public readonly List<IRNodeRef> Inputs = new();

        // The list of output nodes for this sub-graph. Input nodes have no restrictions.
        public readonly List<IRNodeRef> Outputs = new();

        // The implicit entity IR nodes is sort of like a 'this' or 'self' node that is referenced while a sub-graph
        // is constructed. It is usually also an input to the sub-graph.
        // Null if this sub-graph does not reference the entity.
        [CanBeNull]
        public IRNodeRef ImplicitEntityNode;

        /// <summary>
        ///     A layer of indirection to make it easier to replace nodes in the graph without chasing down all the external
        ///     pointers into the graph and updating them. Allows you to redirect all pointers to a node to a new node. Get a node
        ///     reference with GetNodeRef.
        /// </summary>
        private readonly Dictionary<IRNode, List<IRNodeRef>> externalNodeRefs = new();

        /// <summary>
        ///     Set to true by the compiler when the graph is fully constructed, all analysis is complete, and graph is
        ///     lowered for execution.
        /// </summary>
        public bool IsSealed;

        // ------------------------------------------
        // The following fields are caches for various analysis performed after the graph is constructed.
        // ------------------------------------------

        // The IR Nodes that are associated with each graph.
        private readonly Dictionary<LatticeGraph, HashSet<IRNode>> NodesPerGraph = new();

        // The index in the Nodes list of an IRNode.
        public readonly Dictionary<IRNode, int> NodeIndices = new();

        // The set of IRNodes that are referenced by a PreviousNode backref. This is our 'state'.
        public readonly HashSet<IRNode> ReferencedByPreviousNode = new();

        // A full topological sort of the graph. Used for execution ordering.
        private List<IRNode> topologicalSort;

        // todo: can we really keep this private? seems smart.
        /// <summary>
        ///     Returns a node ref to the target node. This allows us to store pointers into the graph in external data
        ///     structures / acceleration structures, and keep those pointing correctly even if the graph is mutated / optimized /
        ///     nodes are replaced.
        /// </summary>
        public IRNodeRef GetNodeRef(IRNode node)
        {
            if (externalNodeRefs.TryGetValue(node, out List<IRNodeRef> r))
            {
                r[0].RefCount++;
                return r[0];
            }

            IRNodeRef irNodeRef = new()
            {
                Node = node,
                RefCount = 1
            };

            var list = new List<IRNodeRef>();
            list.Add(irNodeRef);
            externalNodeRefs[node] = list;

            return irNodeRef;
        }

        public bool HasExternalReferences(IRNode node)
        {
            return externalNodeRefs.TryGetValue(node, out List<IRNodeRef> refs) && refs.Any();
        }

        public int CountExternalReferences(IRNode node)
        {
            return externalNodeRefs.TryGetValue(node, out List<IRNodeRef> refs) ? refs.Count : 0;
        }

        public void RedirectExternalReferences(IRNode node, IRNode redirect)
        {
            // Update node pointers to point to the new node.
            // This is a little complex, as we have to point all refs pointing at this node to the new one. This may be several
            // refs, if previous redirections have happened.
            if (externalNodeRefs.ContainsKey(node))
            {
                foreach (var r in externalNodeRefs[node])
                {
                    r.Node = redirect;
                }
                if (!externalNodeRefs.ContainsKey(redirect))
                {
                    externalNodeRefs[redirect] = new();
                }

                externalNodeRefs[redirect].AddRange(externalNodeRefs[node]);
                externalNodeRefs.Remove(node);
            }
        }

        /// <summary>Deletes a node-ref. Think of it like decrementing a ref-count.</summary>
        public void ReleaseNodeRef(IRNodeRef nodeRef)
        {
            nodeRef.RefCount--;

            List<IRNodeRef> refs = externalNodeRefs[nodeRef.Node];
            if (nodeRef.RefCount == 0)
            {
                if (!refs.Remove(nodeRef))
                {
                    throw new Exception($"Tried to release IRNodeRef that was not a part of this IRGraph! [{nodeRef.Node}]");
                }
            }
            if (refs.Count == 0)
            {
                externalNodeRefs.Remove(nodeRef.Node);
            }
        }

        /// <summary> Creates a mapping for this code path. Necessary because some code paths may generate zero nodes! </summary>
        public void CreateMapping(CodePath path)
        {
            Mappings[path] = new();
        }

        /// <summary>Adds the IRNode to the compilation, mapped to the given authoredNode.</summary>
        public T AddNode<T>(CodePath path, T node, string name = null, string identifier = null)
            where T : IRNode
        {
            Assert.IsFalse(IsSealed, "Nodes cannot be added once the graph is done compiling.");
            Assert.IsNull(node.Id, "Node was already added to the compilation!");

            Nodes.Add(node);

            Mappings[path].Nodes.Add(node);
            Parents[node] = path;

            int idNum = Mappings[path].NodeIdCount++;
            if (identifier != null)
            {
                node.Id = identifier;
            }
            else
            {
                StringBuilder b = new();
                foreach (var n in path)
                {
                    // I think we don't need a guid here, because within a single IRGraph body, this FileId will be 
                    // unique, and when inlining, a mounting id will always be appended. A fileid simplifies the id.
                    b.Append(n.FileId);
                    b.Append('/');
                }
                b.Append(idNum);
                node.Id = b.ToString();
            }

            node.DebugName = name ?? node.GetType().Name.Replace("IRNode", "");
            node.DebugPath = path.GetDebugPath();
            node.DoNotLogErrors = path.Last.DoNotLogErrors;

            return node;
        }

        /// <summary>Adds the IRNode to the compilation, mapped to the given span.</summary>
        public T AddNode<T>(CodePath span, string name, T node) where T : IRNode
        {
            return AddNode(span, node, name: name);
        }

        /// <summary>Adds the IRNode to the compilation, floating freely within the IRGraph with no syntax nodes associated.</summary>
        public T AddFreeNode<T>(T node, string identifier, string debugName)
            where T : IRNode
        {
            Assert.IsFalse(IsSealed, "Nodes cannot be added once the graph is done compiling.");
            Assert.IsNull(node.Id, "Node was already added to the compilation!");

            Nodes.Add(node);

            node.Id = identifier;
            node.DebugPath = node.Id;
            node.DebugName = debugName ?? identifier ?? node.GetType().Name.Replace("IRNode", "");

            return node;
        }

        public IRNode AddFieldAccessor(CodePath span, IRNode input, string fieldName,
                                       string authoredOutputPort = null, string name = null)
        {
            var fieldNode = AddNode(span, new FieldAccessorIRNode(fieldName), name);
            fieldNode.AddInput(FieldAccessorIRNode.PortInput, input);

            // Map the output port for the field on the authored node to this IRNode.
            if (authoredOutputPort != null)
            {
                MapOutputPort(span, authoredOutputPort, fieldNode);
            }

            return fieldNode;
        }

        /// <summary>Sets the IRNode as the 'primary' node for this span.</summary>
        public void SetPrimaryNode(CodePath path, IRNode node)
        {
            Assert.IsFalse(IsSealed, "The graph cannot be modified after construction.");
            
            // This could be ok, but we'd need to dispose the existing ref. Feels like smell.
            Assert.IsNull(Mappings[path].PrimaryNode, "Primary node already set.");
            
            Mappings[path].PrimaryNode = GetNodeRef(node);
        }

        public void SetStateDebugNode(CodePath path, IRNode node)
        {
            Assert.IsFalse(IsSealed, "The graph cannot be modified after construction.");
            
            // This could be ok, but we'd need to dispose the existing ref. Feels like smell.
            Assert.IsNull(Mappings[path].StateDebugNode, "State debug node already set.");
            
            Mappings[path].StateDebugNode = node != null ? GetNodeRef(node) : null;
        }

        public IRNode GetPrimaryNode(CodePath path)
        {
            return Mappings[path].PrimaryNode?.Node;
        }

        public IRNode GetStateDebugNode(CodePath path)
        {
            return Mappings[path].StateDebugNode?.Node;
        }

        public IReadOnlyList<IRNode> GetNodesUnderPath(CodePath path)
        {
            return Mappings[path].Nodes;
        }

        public bool IsNodeOwnedBy(IRNode node, CodePath codePath)
        {
            return Mappings[codePath].Nodes.Contains(node);
        }

        /// <summary>Gets the LatticeNode that created this IRNode.</summary>
        public CodePath? GetOwner(IRNode node)
        {
            if (!Parents.TryGetValue(node, out CodePath owner))
            {
                return null;
            }
            return owner;
        }

        public Dictionary<CodePath, Mapping>.KeyCollection GetCodePaths()
        {
            return Mappings.Keys;
        }

        public bool ContainsCodePath(CodePath path)
        {
            return Mappings.ContainsKey(path);
        }

        public IRNode GetImplicitEntity(LatticeGraph graph)
        {
            if (ImplicitEntityNode == null)
            {
                FunctionIRNode entityNode = AddFreeNode(CoreIRNodes.Identity(typeof(Entity)), "ImplicitEntity", "ImplicitEntity");
                IRNodeRef nodeRef = GetNodeRef(entityNode);
                ImplicitEntityNode = nodeRef;
                return nodeRef.Node;
            }

            return ImplicitEntityNode.Node;
        }

        /// <summary>
        ///     Copies the nodes and edges from another IRGraph ('other') into this graph, associating the copied elements
        ///     with the provided 'span' (CodePath). Adjusts connections, including PreviousIRNode back-references, to point to the
        ///     newly copied nodes within this graph.
        /// </summary>
        /// <param name="mountingId">
        ///     The identifier that will be appended onto all nodes of this graph after it is inlined. This
        ///     makes sure that graphs that are inlined several times have distinct identifiers.
        /// </param>
        /// <param name="mountingSpan">
        ///     The CodePath representing the context/location where the 'other' graph is being inlined. If
        ///     null, the mounted graph will retain its existing span and we mount this subgraph as a toplevel subgraph.
        /// </param>
        /// <param name="other">The IRGraph to inline.</param>
        /// <returns>A list of the newly created IRNodes in this graph.</returns>
        public Dictionary<IRNode, IRNode> CopyIntoGraph(IRGraph other, string mountingId, GraphPath mountingSpan)
        {
            // Prevent modification after graph analysis/optimization is complete.
            Assert.IsFalse(IsSealed, "Cannot inline graphs after the graph is sealed.");

            // Dictionary to map original nodes from 'other' graph to their new copies in 'this' graph.
            var nodeMap = new Dictionary<IRNode, IRNode>();

            // --- 0. Create CodePath Mappings ---
            // We do this separately because some CodePaths have zero IRNodes, but we still want to inline them, because
            // we need to access that information for debugging.
            foreach (var (path, _) in other.Mappings)
            {
                Mappings[new CodePath(mountingSpan, path)] = new Mapping();
            }

            // --- 1. Copy Nodes ---
            foreach (var originalNode in other.Nodes)
            {
                // Clone the original node. The Clone method should handle copying properties
                // but not connections, which will be rebuilt.
                var newNode = originalNode.MemberwiseCloneFresh();

                string newNodeId = mountingId + "/" + originalNode.Id;

                // Debug.Log($"Copying node [{originalNode.Id}] to [{newNodeId}]");
                // Debug.Log($"Parent: [{other.Parents[originalNode]}]");

                // Add the cloned node to 'this' graph under the specified span.
                // AddNode handles assigning a new unique ID and setting parent context.
                if (other.Parents.TryGetValue(originalNode, out CodePath path))
                {
                    // copy mounted node
                    AddNode(new CodePath(mountingSpan, path), newNode, originalNode.DebugName, newNodeId);
                }
                else
                {
                    // copy free node
                    AddFreeNode(newNode, newNodeId, originalNode.DebugName);
                }

                nodeMap[originalNode] = newNode; // Map original to new node
            }

            // -- 1.5 Copy Mappings and References
            // Copy mappings and references
            foreach (var (originalPath, originalMapping) in other.Mappings)
            {
                var newPath = new CodePath(mountingSpan, originalPath);

                // Copy primary node reference
                if (originalMapping.PrimaryNode != null)
                {
                    SetPrimaryNode(newPath, nodeMap[originalMapping.PrimaryNode.Node]);
                }

                // Copy state debug node reference
                if (originalMapping.StateDebugNode != null)
                {
                    SetStateDebugNode(newPath, nodeMap[originalMapping.StateDebugNode.Node]);
                }

                // Copy input port mappings
                foreach (var (port, inputMap) in originalMapping.InputPortMap)
                {
                    MapInputPort(newPath, port, inputMap == null ? null : nodeMap[inputMap.Value.Item1],
                        inputMap?.Item2);
                }

                // Copy output port mappings
                foreach (var (port, originalNodeRef) in originalMapping.OutputPortMap)
                {
                    MapOutputPort(newPath, port, nodeMap[originalNodeRef.Node]);
                }
            }

            // --- 2. Copy Edges ---
            foreach (var originalNode in other.Nodes)
            {
                var newNode = nodeMap[originalNode]; // Get the corresponding new node

                // Iterate through the ports and inputs of the original node
                foreach (var (portId, originalPort) in originalNode.Ports)
                {
                    // Add the port definition to the new node if it doesn't exist
                    // (Clone should ideally handle this, but double-checking)
                    if (!newNode.Ports.ContainsKey(portId))
                    {
                        Debug.Log(originalNode);
                        Debug.LogError(
                            $"ICE: Port [{portId}] not found on node [{newNode}][{newNode.GetType().Name}], after copying. Incorrect Clone() implementation? " +
                            $"Original Ports: [{string.Join(",", originalNode.Ports.Keys)}] " +
                            $"New Ports: [{string.Join(",", newNode.Ports.Keys)}]");
                        continue;
                    }

                    // Connect the inputs for this port on the new node
                    foreach (var originalInputNode in originalPort.Inputs)
                    {
                        // Find the corresponding new input node from the map
                        if (!nodeMap.TryGetValue(originalInputNode, out var newInputNode))
                        {
                            // This would imply that an input edge pointed at an IRNode outside the IRGraph, which is 
                            // impossible (an invariant), so it's a hard error.
                            Debug.LogError(
                                $"ICE: Could not find corresponding new node for original input node '{originalInputNode}' when connecting port '{portId}' of node '{newNode}'." +
                                " Edge skipped.");
                            continue;
                        }

                        newNode.AddInput(portId, newInputNode);
                    }
                }
            }

            // --- 3. Fix Up PreviousIRNode BackRefs ---
            foreach (var kvp in nodeMap)
            {
                if (kvp.Value is PreviousIRNode newPreviousNode)
                {
                    var originalPreviousNode = (PreviousIRNode)kvp.Key;

                    if (originalPreviousNode.BackRef != null)
                    {
                        if (!nodeMap.TryGetValue(originalPreviousNode.BackRef.Node, out var newBackRefNode))
                        {
                            // This would imply that an input edge pointed at an IRNode outside the IRGraph, which is 
                            // impossible (an invariant), so it's a hard error.

                            Debug.LogWarning(
                                $"InlineGraphCompilation: Could not find corresponding new node for the BackRef of original PreviousIRNode '{originalPreviousNode}'. BackRef might be broken.");
                            continue;
                        }

                        // Update the BackRef on the new PreviousIRNode to point to the new back-ref node.
                        newPreviousNode.BackRef = GetNodeRef(newBackRefNode);
                    }
                    else
                    {
                        Debug.LogError("Cannot inline invalid PreviousIRNode. BackRef should be set.");
                    }
                }
            }

            return nodeMap;
        }

        public void ReplaceNodeWithMalformed(CodePath node, Exception reason)
        {
            Assert.IsFalse(IsSealed, "The graph cannot be modified after construction.");

            List<IRNode> existingNodes = Mappings[node].Nodes;

            // Add a malformed node.
            MalformedIRNode malformedNode = new(reason);

            // Clear the mapping. This allows removing these nodes via RedirectNode. We setup the mappings below.
            Mappings[node] = new Mapping();

            // Add the node (adds it to the new mapping, too)
            AddNode(node, "Malformed", malformedNode);

            // Redirect all nodes in this LatticeNode with to the malformed node.
            foreach (IRNode n in existingNodes)
            {
                // Must replace node with a node of the same output type.
                RedirectNode(n, malformedNode);
            }

            // All input ports map nowhere
            foreach (NodePort port in node.Last.GetLogicalInputs())
            {
                MapInputPort(node, port.portData.identifier, null);
            }

            // All output ports map to the malformed node
            foreach (NodePort port in node.Last.GetValueOutputs())
            {
                MapOutputPort(node, port.portData.identifier, malformedNode);
            }
        }

        // leaveInGraph: If true, the node will remain in the graph, but completely disconnected.
        public void RedirectNode(IRNode node, IRNode redirect, bool leaveInGraph = false)
        {
            // Removing nodes is only valid if the node is not pointed at via the input mapping.
            // This is necessary because otherwise your redirection would need to specify how to update the input port mappings.
            Assert.IsTrue(!InputMapContainsNode(node));
            Assert.IsFalse(IsSealed, "The graph cannot be modified after construction.");

            // Update consumers to point to new node. 
            while (node.Usages.Count > 0)
            {
                var usage = node.Usages[^1];
                usage.node.RemoveInput(usage.portId, node);
                usage.node.AddInput(usage.portId, redirect);
            }

            RedirectExternalReferences(node, redirect);

            // Now we can remove the node fully from the graph.
            if (!leaveInGraph)
            {
                RemoveNode(node);
            }
        }

        public void RemoveNode(IRNode node)
        {
            Assert.IsFalse(IsSealed, "The graph cannot be modified after construction.");

            // Removing nodes is only valid if the node is not pointed at via the input mapping.
            // The output mapping must also be empty. That's checked below via external refs.
            Assert.IsTrue(!InputMapContainsNode(node));

            // Straight up removing a node is possible,  but you must ensure that:
            // - No other nodes depend on it.
            // - There are no outstanding external references to this node (primary node, outputportmaps)
            Assert.AreEqual(node.Usages.Count, 0, "Node has existing usages and cannot be removed.");

            // Make sure there's no external references to this node.
            // Note: Node refs are not deleted, currently, once defined.
            // This could be relaxed if we better track when node references are changed, or remove this system
            // in favor of putting everything in the graph.
            if (HasExternalReferences(node))
            {
                throw new Exception($"Node has live external references and can't be removed. [{node}] [{CountExternalReferences(node)}]");
            }

            if (node is PreviousIRNode { BackRef: not null } pNode)
            {
                throw new AssertionException("", $"Cannot remove PreviousIRNode with present BackRef. Release it first. [{pNode}]");
            }

            // Remove all input connections. 
            node.RemoveAllInputs();

            // Remove node from owned node list for LatticeNode mappings.
            if (Parents.TryGetValue(node, out CodePath owner))
            {
                Mappings[owner].Nodes.Remove(node);
                Parents.Remove(node);
            }

            Nodes.Remove(node);
        }

        public bool InputMapContainsNode(IRNode node)
        {
            if (!Parents.TryGetValue(node, out CodePath parent))
            {
                // No parent, so cannot be a part of the input mapping.
                return false;
            }

            var portmap = Mappings[parent].InputPortMap;
            if (portmap == null)
            {
                return false;
            }

            foreach (var (authoredPort, irPort) in portmap)
            {
                if (irPort.HasValue && irPort.Value.node == node)
                {
                    return true;
                }
            }

            return false;
        }

        public void MapOutputPort(CodePath span, string outputPort, IRNode node)
        {
            Assert.AreEqual(Parents[node], span);
            Assert.IsFalse(IsSealed, "The graph cannot be modified after construction.");
            Mappings[span].OutputPortMap[outputPort] = GetNodeRef(node);
        }

        public IReadOnlyDictionary<string, IRNodeRef> GetOutputMap(CodePath node)
        {
            if (Mappings.TryGetValue(node, out var mapping))
            {
                return mapping.OutputPortMap;
            }

            throw new Exception($"ICE: Node [{node}] is not a part of this IRGraph.");
        }

        public IReadOnlyDictionary<string, (IRNode node, string irPort)?> GetInputMap(CodePath node)
        {
            if (Mappings.TryGetValue(node, out var mapping))
            {
                return mapping.InputPortMap;
            }

            throw new Exception($"ICE: Node [{node}] is not a part of this IRGraph.");
        }

        /// <summary>
        ///     For release builds, we no longer need references to Primary and State Debug nodes, so we can delete them from
        ///     the graph to save compilation time.
        /// </summary>
        public void WipeDebugHandles()
        {
            foreach (var (_, m) in Mappings)
            {
                if (m.PrimaryNode != null)
                {
                    ReleaseNodeRef(m.PrimaryNode);
                    m.PrimaryNode = null;
                }

                if (m.StateDebugNode != null)
                {
                    ReleaseNodeRef(m.StateDebugNode);
                    m.StateDebugNode = null;
                }
            }
        }

        public void WipePortMappings()
        {
            // Throw out input port mappings, as we only need them for construction, and after graph optimization they
            // no longer have a sensible meaning. Necessary because some of our optimizations do not keep these as an invariant after transformation.
            foreach (var (_, mapping) in Mappings)
            {
                mapping.InputPortMap = null;
            }

            // Throw out output port mappings, as we only need them for construction. Holding onto these handles keeps 
            // from being removed during dead code elimination and causing the computation of lots of extra values.
            // For debugging, we can attach a debug node to the port if we want to keep the value.
            foreach (var (_, mapping) in Mappings)
            {
                foreach (IRNodeRef r in mapping.OutputPortMap.Values)
                {
                    ReleaseNodeRef(r);
                }
                mapping.OutputPortMap.Clear();
            }
        }

        /// <summary>Maps the input port on the authored node to the same port on the IRNode.</summary>
        /// <remarks>Mapping to a null marks that the port goes nowhere, intentionally.</remarks>
        public void MapInputPort(CodePath span, string authoredPort, [CanBeNull] IRNode node,
                                 string irPort = null)
        {
            Assert.IsFalse(IsSealed, "The graph cannot be modified after construction.");
            if (node == null)
            {
                Mappings[span].InputPortMap[authoredPort] = null;
                return;
            }

            Assert.AreEqual(Parents[node], span);
            string irPortName = irPort ?? authoredPort;
            if (!node.Ports.ContainsKey(irPortName))
            {
                var validPorts = string.Join(",", node.Ports.Keys);
                throw new Exception($"Node does not have port [{irPortName}]. " +
                                    $"[{node.DebugPath}] when mapping port [{authoredPort}] on [{span}]. Ports: [{validPorts}]");
            }
            Mappings[span].InputPortMap[authoredPort] = (node, irPortName);
        }

        /// <summary>Adds an input mapping for every input port on the LatticeNode.</summary>
        public void MapInputPorts(CodePath span, IRNode node)
        {
            foreach (NodePort input in span.Last.GetLogicalInputs())
            {
                MapInputPort(span, input.portData.identifier, node);
            }
        }

        public enum Ordering
        {
            Input,
            Usage,
            None,
        }

        /// <summary>
        ///     Returns how node A is ordered with respect to node B. If node A is an input of B (transitively), it's a Child.
        ///     If it's a usage of
        /// </summary>
        public Ordering GetPartialOrder(IRNode nodeA, IRNode nodeB)
        {
            Assert.IsTrue(IsSealed);

            // Trivial case:
            if (nodeA == nodeB)
            {
                return Ordering.None;
            }

            using (CollectionPool<HashSet<IRNode>, IRNode>.Get(out var visited))
            {
                // If nodeA depends on nodeB, then nodeA is an input. (ie. A is earlier than B)
                if (DependsOn(nodeA, nodeB, visited))
                {
                    return Ordering.Input;
                }

                visited.Clear();

                // The inverse: If nodeB depends on nodeA, then nodeA is a usage.
                if (DependsOn(nodeB, nodeA, visited))
                {
                    return Ordering.Usage;
                }
            }

            return Ordering.None;
        }

        private bool DependsOn(IRNode a, IRNode b, HashSet<IRNode> visited)
        {
            if (!visited.Add(a))
            {
                return false;
            }

            foreach (var input in a.InputNodes())
            {
                if (input == b)
                {
                    return true;
                }
                if (DependsOn(input, b, visited))
                {
                    return true;
                }
            }

            if (a is PreviousIRNode prev)
            {
                if (prev.BackRef.Node == b)
                {
                    return true;
                }
                if (DependsOn(prev.BackRef.Node, b, visited))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Gets the list of nodes in the graph, ordered topologically, for execution.</summary>
        public List<IRNode> GetTopologicalSort()
        {
            Assert.IsTrue(IsSealed);

            if (topologicalSort != null)
            {
                return topologicalSort;
            }

            topologicalSort = TopologicalSortNodes(Nodes);
            return topologicalSort;
        }

        public HashSet<IRNode> GetNodesInGraph(LatticeGraph graphAsset)
        {
            Assert.IsTrue(IsSealed, "Graph must be sealed to use GetNodesInGraph.");

            if (NodesPerGraph.TryGetValue(graphAsset, out HashSet<IRNode> cachedNodes))
            {
                return cachedNodes;
            }

            HashSet<IRNode> result = new();
            foreach (var node in Nodes)
            {
                if (GetOwner(node)?.Last.Graph == graphAsset)
                {
                    result.Add(node);
                }
            }

            NodesPerGraph[graphAsset] = result;

            return result;
        }

        public static List<IRNode> TopologicalSortNodes(ICollection<IRNode> nodeList)
        {
            // Topological sort for ordering.
            using var _ = CollectionPool<HashSet<IRNode>, IRNode>.Get(out var visited);
            using var __ = CollectionPool<HashSet<IRNode>, IRNode>.Get(out var nodes);

            foreach (var n in nodeList)
            {
                nodes.Add(n);
            }

            var next = new Queue<IRNode>(nodeList.Where(n => n.InputNodes().Count(i => nodes.Contains(i)) == 0));
            var sort = new List<IRNode>();

            while (next.TryDequeue(out IRNode node))
            {
                if (visited.Contains(node))
                {
                    continue;
                }

                sort.Add(node);
                visited.Add(node);

                foreach ((var ___, IRNode consumer) in node.Usages)
                {
                    if (consumer.InputNodes().All(n => visited.Contains(n)))
                    {
                        next.Enqueue(consumer);
                    }
                }
            }

            Assert.AreEqual(nodeList.Count, visited.Count, "ICE: Topological sort failed. (didn't visit all nodes)");
            Assert.AreEqual(sort.Count, nodeList.Count,
                "ICE: Topological sort failed. (didn't sort all nodes)");

            return sort;
        }

        /// <summary>Checks that run at the end of optimization (before IL Generation) to verify that the graph setup is sound.</summary>
        public bool VerifyGraphStructure(GraphCompiler.Settings settings)
        {
            bool failed = false;

            using var _ = CollectionPool<HashSet<IRNode>, IRNode>.Get(out var nodes);

            foreach (var n in Nodes)
            {
                nodes.Add(n);
            }

            foreach ((CodePath span, Mapping mapping) in Mappings)
            {
                if (mapping.PrimaryNode == null && settings.Debug)
                {
                    Debug.LogError($"ICE: Primary node not set for Lattice Node [{span}]. Primary nodes must be set in Debug mode.");
                    failed = true;
                }

                if (mapping.StateDebugNode != null && !nodes.Contains(mapping.StateDebugNode.Node))
                {
                    Debug.LogError(
                        $"ICE: Debug node points to a node outside the graph. Lattice Node [{span}] points at debug node [{mapping.StateDebugNode.Node}]");
                    failed = true;
                }
                if (mapping.PrimaryNode != null && !nodes.Contains(mapping.PrimaryNode.Node))
                {
                    Debug.LogError(
                        $"ICE: Debug node points to a node outside the graph. Lattice Node [{span}] points at debug node [{mapping.PrimaryNode.Node}]");
                    failed = true;
                }
            }

            // Nodes only depend on the nodes in the graph. 
            foreach (var n in Nodes)
            {
                foreach (var (portId, p) in n.Ports)
                {
                    foreach (var input in p.Inputs)
                    {
                        if (!nodes.Contains(input))
                        {
                            Debug.LogError(
                                $"ICE: Node input is not in the graph. Node [{n}] with input [{input}] on port [{portId}].");
                            failed = true;
                        }
                    }
                }

                // Nodes are only used by other nodes in the graph.
                foreach ((string portId, IRNode node) in n.Usages)
                {
                    if (!nodes.Contains(node))
                    {
                        Debug.LogError(
                            $"ICE: Node usage is not in the graph. Usage [{node}] on port [{portId}] of node [{node}].");
                        failed = true;
                    }
                }
            }

            if (GraphCompiler.EnableVerboseChecks)
            {
                // Verify that usages are setup correctly
                foreach (IRNode n in Nodes)
                {
                    foreach ((string id, IRPort port) in n.Ports)
                    {
                        foreach (IRNode input in port.Inputs)
                        {
                            if (!input.HasUsage(n))
                            {
                                Debug.LogError(
                                    $"ICE: Node usages are malformed. Node [{input}] should have usage for dependent node [{n}][{id}].");
                                failed = true;
                            }
                        }
                    }
                }

                // Verify that nodes are properly removed from the graph. (all refs are in the node list)
                foreach (var n in Parents.Keys)
                {
                    if (!nodes.Contains(n))
                    {
                        // Often found if you try to remove something just from the Nodes list.
                        Debug.LogError(
                            $"ICE: Node was not properly removed from the graph. [{n}] Node existed in parents list, but not present in node list.");
                        failed = true;
                    }
                }

                // No lingering noderefs pointers to things outside the graph. 
                foreach (var (node, refs) in externalNodeRefs)
                {
                    if (!nodes.Contains(node))
                    {
                        Debug.LogError(
                            $"ICE: Node in nodeRefs dictionary was not in graph. [{node}].");
                        failed = true;
                    }

                    foreach (var r in refs)
                    {
                        if (!nodes.Contains(r.Node))
                        {
                            Debug.LogError(
                                $"ICE: Node in nodeRefs dictionary was not in graph. [{r}]. Ref: [{node}]");
                            failed = true;
                        }
                    }
                }
            }

            return !failed;
        }
    }
}
