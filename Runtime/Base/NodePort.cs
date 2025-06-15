// #define DEBUG_LAMBDA

using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Lattice.Nodes;

namespace Lattice.Base
{
    /// <summary>Class that describe port attributes for it's creation</summary>
    public class PortData : IEquatable<PortData>
    {
        /// <summary>Data not associated with a real serialized port. A port that's created to show dangling connections.</summary>
        internal static PortData GetVirtualPortData(string identifier, bool isVertical)
        {
            return new PortData(identifier, isVertical, true, typeof(ITypeUnknown))
            {
                IsVirtual = true,
                hasDefault = false,
                acceptMultipleEdges = true // Virtual ports allow multiple edges since we don't know what the real port is.
            };
        }

        /// <summary>Unique identifier for the port</summary>
        public string identifier;

        /// <summary>Display name on the node</summary>
        public string displayName;

        /// <summary>The default type of the port. If null, the type will be inferred from connect IR nodes.</summary>
        [CanBeNull]
        public Type defaultType;

        /// <summary>If the port accept multiple connection</summary>
        public bool acceptMultipleEdges;
        
        /// <summary>Tooltip of the port</summary>
        [CanBeNull]
        public string customTooltip;

        /// <summary>Is the port vertical</summary>
        public bool vertical;

        /// <summary>If this port is a side-channel. Used for action edges, state ports. The ports are shifted to the left/right.</summary>
        public bool secondaryPort;

        /// <summary>Whether this node must be connected.</summary>
        public bool optional;

        public bool hasDefault;

        // Ref types are a bit weird. They act as the opposite port type. So a ref input shows up as an *output* because it writes to another node.
        public bool isRefType;

        /// <summary>If the port was created using <see cref="GetVirtualPortData"/>. Used in cases where a dangling edge has created a virtual port.</summary>
        public bool IsVirtual { get; private set; }

        public PortData(string identifier, bool vertical = true, bool optional = false, Type defaultType = null)
        {
            this.identifier = identifier;
            this.vertical = vertical;
            this.optional = optional;
            this.defaultType = defaultType;
        }

        public bool Equals(PortData other)
        {
            return other != null
                   && identifier == other.identifier
                   && displayName == other.displayName
                   && defaultType == other.defaultType
                   && acceptMultipleEdges == other.acceptMultipleEdges
                   && customTooltip == other.customTooltip
                   && vertical == other.vertical
                   && secondaryPort == other.secondaryPort
                   && optional == other.optional;
        }

        public void CopyFrom(PortData other)
        {
            identifier = other.identifier;
            displayName = other.displayName;
            defaultType = other.defaultType;
            acceptMultipleEdges = other.acceptMultipleEdges;
            customTooltip = other.customTooltip;
            vertical = other.vertical;
            secondaryPort = other.secondaryPort;
            optional = other.optional;
        }
    }

    /// <summary>Runtime class that stores all info about one port that is needed for the processing</summary>
    public class NodePort
    {
        /// <summary>The node on which the port is</summary>
        public BaseNode owner;

        /// <summary>Data of the port</summary>
        public PortData portData;

        private readonly List<SerializableEdge> edges = new();

        /// <summary>Constructor</summary>
        /// <param name="owner">owner node</param>
        /// <param name="portData">Data of the port</param>
        public NodePort(BaseNode owner, PortData portData)
        {
            this.owner = owner;
            this.portData = portData;
        }

        /// <summary>Connect an edge to this port</summary>
        /// <param name="edge"></param>
        public void Add(SerializableEdge edge)
        {
            if (!edges.Contains(edge))
            {
                edges.Add(edge);
            }
        }

        /// <summary>Disconnect an Edge from this port</summary>
        /// <param name="edge"></param>
        public void Remove(SerializableEdge edge)
        {
            edges.Remove(edge);
        }

        /// <summary>Get all the edges connected to this port</summary>
        /// <returns></returns>
        public List<SerializableEdge> GetEdges()
        {
            return edges;
        }
    }
}
