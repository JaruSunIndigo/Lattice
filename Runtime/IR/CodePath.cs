using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Lattice.Nodes;
using UnityEngine.Assertions;
using UnityEngine.Pool;

namespace Lattice.IR
{
    //todo: I'm fairly sure this should be either rooted or not. So Root can be null and that's a relative reference.

    // We don't implement IEnumerable, because that would cause boxing. You must use foreach instead, with a pooled list.

    /// <summary>
    ///     A specific path to an executed LatticeNode from the root of the code graph. Think of this like a path in the
    ///     abstract syntax tree of LatticeGraph code.
    /// </summary>
    public readonly struct CodePath : IEquatable<CodePath>
    {
        // ie. [Timer1:LatticeSubgraphNode] -> [TimerFunc:ScriptNode]
        // ie. [RootGraph:ChargerAsset] -> [Timer1:LatticeSubgraphNode] -> [TimerFunc:ScriptNode]

        // The top level of a code path is a LatticeGraph root in normal code.
        // Is it valid for the node list to be empty? This would not be a reference to a specific syntax node anymore..
        // Hmmm...

        // todo: When the list allocation here becomes too expensive, we can allocate these in the GraphCompilation
        // as a bump allocator of List<LatticeNode> and just store indices here.
        // Actually we could just do a struct of 8~ LatticeNode fields! Paths longer than 8 would be kind of absurd.

        public LatticeGraph Root { get; }

        // Inlined list, so that this struct is cheap to create and copy. No alloc.
        // All nodes except the last node must be subgraph nodes.
        private readonly LatticeNode node1;
        private readonly LatticeNode node2;
        private readonly LatticeNode node3;
        private readonly LatticeNode node4;
        private readonly LatticeNode node5;

        public int Count
        {
            get
            {
                if (node1 == null)
                {
                    throw new Exception("CodePath should not have zero length");
                }
                if (node2 == null)
                {
                    return 1;
                }
                if (node3 == null)
                {
                    return 2;
                }
                if (node4 == null)
                {
                    return 3;
                }
                if (node5 == null)
                {
                    return 4;
                }
                return 5;
            }
        }

        public LatticeNode Last
        {
            get
            {
                if (node5 != null)
                {
                    return node5;
                }
                if (node4 != null)
                {
                    return node4;
                }
                if (node3 != null)
                {
                    return node3;
                }
                if (node2 != null)
                {
                    return node2;
                }
                return node1;
            }
        }

        // todo: Replace these constructors with two CodePath.Relative and CodePath.Rooted.
        /// <summary>
        /// Creates a new rooted code path. Ie. (Graph)/Node1/Node2
        /// </summary>
        public CodePath(LatticeGraph root, IList<LatticeNode> nodes)
        {
            Root = root;

            if (nodes.Count == 0)
            {
                throw new Exception("Cannot create CodePath with empty node list.");
            }

            if (nodes.Count > 5)
            {
                // If we hit this error, we need to expand the nesting limit of subgraphs.
                throw new Exception("Cannot create CodePath with depth greater than 5.");
            }

            node1 = nodes[0];
            node2 = null;
            node3 = null;
            node4 = null;
            node5 = null;

            if (nodes.Count > 1)
            {
                if (nodes[1] == null)
                {
                    return; 
                }
                node2 = nodes[1];
            }
            else
            {
                return; 
            }

            if (nodes.Count > 2)
            {
                if (nodes[2] == null)
                {
                    return;
                }
                node3 = nodes[2];
            }
            else
            {
                return; 
            }

            if (nodes.Count > 3)
            {
                if (nodes[3] == null)
                {
                    return;
                }
                node4 = nodes[3];
            }
            else
            {
                return; 
            }

            if (nodes.Count > 4)
            {
                if (nodes[4] == null)
                {
                    return;
                }
                node5 = nodes[4];
            }
        }

        /// <summary>
        /// Creates a new relative code path. ie "Node"
        /// </summary>
        private CodePath(LatticeNode node)
        {
            Root = null;
            
            node1 = node;
            node2 = null;
            node3 = null;
            node4 = null;
            node5 = null;
        }
        
        
        /// <summary>
        /// Creates a new rooted code path. ie (Graph)/Node
        /// </summary>
        public CodePath(LatticeGraph root, LatticeNode node)
        {
            Root = root;
            
            node1 = node;
            node2 = null;
            node3 = null;
            node4 = null;
            node5 = null;
        }
        
        /// <summary>
        /// Joins a GraphPath with a CodePath.
        /// </summary>
        public CodePath(GraphPath graphPath, CodePath node) 
        {
            Assert.AreEqual(graphPath.Length, 1, "todo: Implement multinode graph paths");
            Root = graphPath.Root;
            
            node1 = node.node1;
            node2 = node.node2;
            node3 = node.node3;
            
            node4 = node.node4;
            node5 = node.node5;

        }

        /// <summary>
        /// Joins two codepaths together.
        /// </summary>
        public CodePath(CodePath parent, CodePath child)
        {
            Root = parent.Root;

            using var _ = CollectionPool<List<LatticeNode>, LatticeNode>.Get(out List<LatticeNode> nodes);

            foreach (var p in parent)
            {
                nodes.Add(p);
            }
            foreach (var c in child)
            {
                nodes.Add(c);
            }

            if (nodes.Count > 5)
            {
                throw new Exception($"Cannot create combined CodePath with depth {nodes.Count}. Maximum depth is 5.");
            }

            node1 = nodes[0];
            node2 = nodes.Count > 1 ? nodes[1] : null;
            node3 = nodes.Count > 2 ? nodes[2] : null;
            node4 = nodes.Count > 3 ? nodes[3] : null;
            node5 = nodes.Count > 4 ? nodes[4] : null;
        }

        /// <summary>
        /// Joins two code paths together.
        /// </summary>
        public CodePath(CodePath parent, LatticeNode child) : this(parent, new CodePath(child)) { }

        public bool Equals(CodePath other)
        {
            return Equals(node1, other.node1) && Equals(node2, other.node2) && Equals(node3, other.node3) &&
                   Equals(node4, other.node4) && Equals(node5, other.node5) && Equals(Root, other.Root);
        }

        public override bool Equals(object obj)
        {
            return obj is CodePath other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = node1 != null ? node1.GetHashCode() : 0;
                hashCode = (hashCode * 397) ^ (node2 != null ? node2.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (node3 != null ? node3.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (node4 != null ? node4.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (node5 != null ? node5.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (Root != null ? Root.GetHashCode() : 0);
                return hashCode;
            }
        }

        // We don't implement IEnumerable, because that would cause boxing. You must use foreach instead, with a pooled list.
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        // We don't implement IEnumerable, because that would cause boxing. You must use foreach instead, with a pooled list.
        public struct Enumerator : IEnumerator<LatticeNode>
        {
            private int index;
            private readonly CodePath path;

            public Enumerator(CodePath path)
            {
                index = -1;
                this.path = path;
            }

            public bool MoveNext()
            {
                index++;
                if (index >= 5)
                {
                    return false;
                }

                return path[index] != null; // Stop if current node slot is null
            }

            public void Reset()
            {
                index = -1;
            }

            public LatticeNode Current => path[index];

            object IEnumerator.Current => Current;

            public void Dispose() { }
        }

        public LatticeNode this[int index]
        {
            get
            {
                switch (index)
                {
                    case 0: return node1;
                    case 1: return node2;
                    case 2: return node3;
                    case 3: return node4;
                    case 4: return node5;
                    default: throw new IndexOutOfRangeException();
                }
            }
        }

        public static bool operator ==(CodePath left, CodePath right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(CodePath left, CodePath right)
        {
            return !left.Equals(right);
        }

        /// <summary>Gets a string representation of this path suitable for displaying in logs and error messages to the user.</summary>
        /// <returns></returns>
        public string GetDebugPath()
        {
            StringBuilder b = new();

            b.Append('(');
            b.Append(Root != null ? Root.name : "[Relative]");
            b.Append(')');

            foreach (var node in this)
            {
                b.Append('/');
                b.Append(string.IsNullOrEmpty(node.Name) ? node.GetType().Name : node.Name);
            }

            return b.ToString();
        }

        public override string ToString()
        {
            return GetDebugPath();
        }
    }
}
