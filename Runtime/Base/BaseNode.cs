using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Annotations;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Serialization;
using Hash128 = Unity.Entities.Hash128;

namespace Lattice.Base
{
    /// <summary>
    ///     Describes how a node is collapsed (only valid if it has a single input or output).
    ///     Only the visuals are affected by this state.
    /// </summary>
    public enum NodeCollapseToDirection
    {
        /// <summary>Node isn't collapsed.</summary>
        None,
        /// <summary>Node is collapsed to the output port of a connected node.</summary>
        ToOutput,
        /// <summary>Node is collapsed to the input port of a connected node.</summary>
        ToInput
    }
    
    /// <summary>
    ///     A node in the graph. This is the "model" and is available at runtime. See BaseNodeView for how a node is
    ///     rendered in the graph view in Editor.
    /// </summary>
    [Serializable]
    public abstract class BaseNode
    {
        // A stable ID within the graph for this node, used for serialization and referencing.
        // This is not globally unique, because it is duplicated when the graph asset is duplicated.
        [FormerlySerializedAs("GUID")]
        public string FileId;

        [SerializeField]
        [CanBeNull]
        [FormerlySerializedAs("CustomName")]
        private string customName;

        // The node position, serialized to disk. 
        [FormerlySerializedAs("position")]
        public float2 Position;

        [SerializeField]
        private NodeCollapseToDirection collapsedToState;

        /// <summary>A parent reference to the graph this node is a part of.</summary>
        [NonSerialized]
        public BaseGraph Graph;

        /// <summary>Container of input ports</summary>
        [NonSerialized]
        public readonly List<NodePort> InputPorts = new();

        /// <summary>Container of output ports</summary>
        [NonSerialized]
        public readonly List<NodePort> OutputPorts = new();

        /// <summary>The name of the node in case if it was renamed by a user.</summary>
        public string CustomName
        {
            get => customName;
            set
            {
                if (value == DefaultName)
                {
                    value = "";
                }

                if (value == customName)
                {
                    return;
                }

                customName = value;
                OnPropertiesChanged?.Invoke(this);
            }
        }

        /// <summary>The name of the node. Defers to custom renamed name, if one has been set by the user.</summary>
        public string Name => !string.IsNullOrEmpty(CustomName) ? CustomName : DefaultName;

        /// <summary>Name of the node, if it's not renamed. Usually the node type. It will be displayed in the title section</summary>
        public virtual string DefaultName => GetType().Name;

        /// <summary>A globally unique identifier for this node, among all files in the project.</summary>
        public string Guid => $"{Graph.HashGuid}:{FileId}";

        // A binary version of the fileid guid for fast usage.
        private Hash128 fileIdHash;
        
        /// <summary>
        ///     Returns a Hash128 that is a globally unique identifier for this node, for the entire project. This identifier
        ///     must also be stable between editor and the standalone build, as it is used to look up data from baking.
        /// </summary>
        public Hash128 HashGuid()
        {
            // Hash the file id if we haven't already. We can remove this long term, once all assets are reserialized with the Hash128 version
            // instead of the string. (todo)
            if (fileIdHash == new Hash128())
            {
                var hasher = new xxHash3.StreamingState();
                hasher.Update(new FixedString64Bytes(FileId));
                fileIdHash = new Hash128(hasher.DigestHash128());
            }
            
            var h = new xxHash3.StreamingState();
            h.Update(fileIdHash);
            h.Update(Graph.HashGuid);
            return new Hash128(h.DigestHash128());
        }

        /// <summary>The accent color of the node</summary>
        public virtual Color Color => Color.clear;

        /// <summary>
        ///     Set a custom uss file for the node. We use a Resources.Load to get the stylesheet so be sure to put the
        ///     correct resources path https://docs.unity3d.com/ScriptReference/Resources.Load.html
        /// </summary>
        public virtual string LayoutStyle => string.Empty;

        /// <summary>If the node can be locked or not</summary>
        public virtual bool Lockable => false;

        /// <summary>
        ///     Describes how a node is collapsed (merged into the port of a connected node).
        ///     Only the graph visuals are affected by this state.<br/>
        ///     This state will be reset if <see cref="AllowsCollapsedNodesOnPorts"/> of the target node is false,
        ///     if there's more than 1 edge attached in the target direction, if there's a side port,
        ///     or if there are more than 2 ports on either side of the node.
        /// </summary>
        public NodeCollapseToDirection CollapsedToState
        {
            get => collapsedToState;
            set => collapsedToState = value;
        }

        /// <summary>If false, nodes are not allowed to collapse on the ports of this node.</summary>
        public virtual bool AllowsCollapsedNodesOnPorts => true;

        /// <summary>True if the node can be deleted, false otherwise</summary>
        public virtual bool Deletable => true;

        /// <summary>Can the node be renamed in the UI. By default a node can be renamed by double clicking it's name.</summary>
        public virtual bool IsRenamable => true;

        /// <summary>Triggered after an edge was connected to the node</summary>
        public event Action<SerializableEdge> OnAfterEdgeConnected;

        /// <summary>Triggered after an edge was disconnected from the node</summary>
        public event Action<SerializableEdge> OnAfterEdgeDisconnected;

        /// <summary>Triggered if some of the properties are changed, like the title.</summary>
        public event Action<BaseNode> OnPropertiesChanged;

        /// <summary>Triggered when the ports are modified on this node.</summary>
        public event Action OnPortsUpdated;

        /// <summary>Called when the node is created from the UI. But not called when it is loaded.</summary>
        public void OnNodeCreated()
        {
            FileId = System.Guid.NewGuid().ToString();
            
            var hasher = new xxHash3.StreamingState();
            hasher.Update(new FixedString64Bytes(FileId));
            fileIdHash = new Hash128(hasher.DigestHash128());
        }

        /// <summary>Call this if you modify this node's properties. Usually causes a recompile.</summary>
        public void PropertiesHaveChanged()
        {
            OnPropertiesChanged?.Invoke(this);
        }

        /// <summary>Called by the BaseGraph when the node is added to the graph. Phase 2 of setup after Deserialize.</summary>
        /// Should be idempotent. After undo, this may be called a second time, to re-initialize caches / ports.
        public void Initialize()
        {
            InputPorts.Clear();
            OutputPorts.Clear();

            UpdateAllPorts();
        }

        /// <summary>
        ///     Passing connected edges allows nodes to change port layout based on what is connected. Such as adding an extra
        ///     empty port, or unpacking a struct input.
        /// </summary>
        protected virtual IEnumerable<PortData> GenerateInputPorts()
        {
            yield break;
        }

        /// <summary>
        ///     Passing connected edges allows nodes to change port layout based on what is connected. Such as adding an extra
        ///     empty port, or unpacking a struct input.
        /// </summary>
        protected virtual IEnumerable<PortData> GenerateOutputPorts()
        {
            yield break;
        }

        protected bool UpdateAllPorts()
        {
            bool changed = UpdatePorts(InputPorts, GenerateInputPorts);
            changed |= UpdatePorts(OutputPorts, GenerateOutputPorts);
            return changed;
        }

        /// <summary>Update the ports type and properties related to one C# property field (only for this node, non-recursive). </summary>
        /// <remarks>Ports are updated when changing types dynamically, such as when using a relay node.</remarks>
        /// <returns>anything updated?</returns>
        private bool UpdatePorts(List<NodePort> portContainer, Func<IEnumerable<PortData>> portGenerator)
        {
            bool changed = false;

            var finalPorts = new List<string>();

            try
            {
                foreach (PortData port in portGenerator())
                {
                    if (portContainer == OutputPorts)
                    {
                        port.acceptMultipleEdges = true;
                    }

                    // Add, Patch, or Remove ports.
                    NodePort existingPort = portContainer.FirstOrDefault(n => n.portData.identifier == port.identifier);

                    if (existingPort == null)
                    {
                        portContainer.Add(new NodePort(this, port));
                        changed = true;
                    }
                    else
                    {
                        // Warn if the ports have changed to make a connected edge incompatible.
                        foreach (SerializableEdge edge in existingPort.GetEdges())
                        {
                            if (!edge.EdgeTypesAreValid())
                            {
                                Debug.LogError(
                                    $"Port [{port.identifier}] changed type [{existingPort.portData.defaultType}]->[{port.defaultType}], making edge invalid. [{edge}][{this}]");
                            }
                        }

                        // patch the port data
                        if (existingPort.portData != port)
                        {
                            existingPort.portData.CopyFrom(port);
                            changed = true;
                        }
                    }

                    finalPorts.Add(port.identifier);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Generating ports failed for node: [{this}]");
                Debug.LogException(e);
            }

            // Remove the ports that are no more in the list
            List<NodePort> currentPortsCopy = portContainer.ToList();
            foreach (NodePort p in currentPortsCopy)
            {
                // If the current port does not appear in the list of final ports, we remove it
                if (finalPorts.All(id => id != p.portData.identifier))
                {
                    portContainer.Remove(p);
                    changed = true;
                }
            }

            // Make sure the port order is correct:
            portContainer.Sort((p1, p2) =>
            {
                int p1Index = finalPorts.FindIndex(id => p1.portData.identifier == id);
                int p2Index = finalPorts.FindIndex(id => p2.portData.identifier == id);

                if (p1Index == -1 || p2Index == -1)
                {
                    return 0;
                }

                return p1Index.CompareTo(p2Index);
            });

            OnPortsUpdated?.Invoke();

            return changed;
        }

        public void OnEdgeConnected(SerializableEdge edge)
        {
            Assert.IsTrue(edge.toNode == this || edge.fromNode == this);

            string myPortId = edge.toNode == this ? edge.toPortIdentifier : edge.fromPortIdentifier;
            GetPort(myPortId).Add(edge);

            UpdateAllPorts();

            OnAfterEdgeConnected?.Invoke(edge);
        }

        public void OnEdgeDisconnected(SerializableEdge edge)
        {
            if (edge == null)
            {
                return;
            }

            Assert.IsTrue(edge.toNode == this || edge.fromNode == this);

            string myPortId = edge.toNode == this ? edge.toPortIdentifier : edge.fromPortIdentifier;
            GetPort(myPortId)?.Remove(edge);

            UpdateAllPorts();

            OnAfterEdgeDisconnected?.Invoke(edge);
        }

        /// <summary>Get the port with the given identifier.</summary>
        public NodePort GetPort(string identifier)
        {
            return GetAllPorts().FirstOrDefault(p => identifier == p.portData.identifier);
        }

        /// <summary>Returns all the ports on the node.</summary>
        public IEnumerable<NodePort> GetAllPorts()
        {
            return InputPorts.Concat(OutputPorts);
        }

        /// <summary>The unique path of the node in the graph.</summary>
        public string GetPath()
        {
            return $"{Graph.name}/{(string.IsNullOrEmpty(Name) ? GetType().Name : Name)}";
        }
    }
}
