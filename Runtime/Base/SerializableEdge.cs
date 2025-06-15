using System;
using System.Collections.Generic;
using Lattice.Nodes;
using Unity.Assertions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Lattice.Base
{
    [Serializable]
    public class SerializableEdge : ISerializationCallbackReceiver
    {
        [FormerlySerializedAs("GUID")]
        public string FileId;

        [SerializeField]
        private BaseGraph owner;

        [FormerlySerializedAs("toNodeGUID")]
        [FormerlySerializedAs("inputNodeGUID")]
        [SerializeField]
        private string toNodeFileId;

        [FormerlySerializedAs("fromNodeGUID")]
        [FormerlySerializedAs("outputNodeGUID")]
        [SerializeField]
        private string fromNodeFileId;

        // Use to store the id of the field that generate multiple ports
        [FormerlySerializedAs("inputPortIdentifier")]
        public string toPortIdentifier;

        [FormerlySerializedAs("outputPortIdentifier")]
        public string fromPortIdentifier;

        /// <summary>Whether the edge is tunnelled across the graph. If hidden, only the ends of the edge are visible by default.</summary>
        public bool IsHidden;

        [NonSerialized]
        public BaseNode fromNode;

        [NonSerialized]
        public NodePort fromPort;

        [NonSerialized]
        public BaseNode toNode;

        [NonSerialized]
        public NodePort toPort;

        public void OnBeforeSerialize()
        {
            if (fromNode == null || toNode == null)
            {
                return;
            }

            fromNodeFileId = fromNode.FileId;
            toNodeFileId = toNode.FileId;
        }

        public void OnAfterDeserialize() { }

        public static SerializableEdge CreateNewEdge(BaseGraph graph, NodePort fromPort, NodePort toPort)
        {
            SerializableEdge edge = new()
            {
                owner = graph,
                FileId = Guid.NewGuid().ToString(),
                toNode = toPort.owner,
                fromNode = fromPort.owner,
                toPort = toPort,
                fromPort = fromPort,
                toPortIdentifier = toPort.portData.identifier,
                fromPortIdentifier = fromPort.portData.identifier,
                IsHidden = false
            };

            return edge;
        }

        /// <summary>Remaps <see cref="fromNode"/> and <see cref="toNode"/> using the input map.</summary>
        public void RemapNodes(BaseGraph graph, Dictionary<string, BaseNode> map)
        {
            owner = graph;

            var reserialize = false;
            if (map.TryGetValue(toNodeFileId, out BaseNode toMappedNode))
            {
                toNodeFileId = toMappedNode.FileId;
                reserialize = true;
            }
            
            if (map.TryGetValue(fromNodeFileId, out BaseNode fromMappedNode))
            {
                fromNodeFileId = fromMappedNode.FileId;
                reserialize = true;
            }

            if (reserialize)
            {
                Deserialize();
            }
        }

        public bool EdgeTypesAreValid()
        {
            return BaseGraph.TypesAreConnectable(fromPort.portData.defaultType, toPort.portData.defaultType);
        }

        //here our owner have been deserialized
        public void Deserialize()
        {
            if (owner.NodesPerFileId.TryGetValue(fromNodeFileId, out BaseNode from))
            {
                fromNode = from;
                fromPort = fromNode.GetPort(fromPortIdentifier);
            }
            
            if (owner.NodesPerFileId.TryGetValue(toNodeFileId, out BaseNode to))
            {
                toNode = to;
                toPort = toNode.GetPort(toPortIdentifier);
            }
        }

        /// <summary>If a port the edge was connected to is deleted or renamed, the edge will be dangling.</summary>
        public bool IsDangling()
        {
            return toPort == null || fromPort == null;
        }

        public bool IsRefEdge()
        {
            // Ports will be null if the edge is dangling -- the port it was connected to was deleted.
            if (IsDangling())
            {
                return false;
            }

            return toPort.portData.isRefType;
        }

        public override string ToString()
        {
            return
                $"{fromNode?.DefaultName}:{fromPort?.portData.identifier ?? "(invalid port) " + fromPortIdentifier} -> {toNode?.DefaultName}:{toPort?.portData.identifier ?? "(invalid port) " + toPortIdentifier}";
        }
    }
}
