using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Base;
using Lattice.Nodes;

namespace Lattice
{
    /// <summary>The top-level graph asset for Lattice Graphs. Holds nodes and edges.</summary>
    public abstract class LatticeGraph : BaseGraph
    {
        public IEnumerable<LatticeNode> LatticeNodes()
        {
            foreach (var node in nodes)
            {
                if (node is LatticeNode n)
                {
                    yield return n;
                }
            }
        }

        public IEnumerable<T> LatticeNodes<T>()
            where T : LatticeNode
        {
            foreach (var node in nodes)
            {
                if (node is T n)
                {
                    yield return n;
                }
            }
        }

        public IEnumerable<LatticeGraph> GetDependencies()
        {
            foreach (var node in LatticeNodes())
            {
                foreach (var dep in node.GetDependencies())
                {
                    yield return dep;
                }
            }
        }

        public LatticeNode GetNode(string name)
        {
            var n = LatticeNodes().FirstOrDefault(n => n.Name == name);
            if (n == null)
            {
                throw new Exception($"Node [{name}] does not exist in graph [{this}].");
            }

            return n;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"({nameof(LatticeGraph)}:{name})";
        }
    }
}
