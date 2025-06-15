namespace Lattice.IR
{
    /// <summary>
    /// A path to a graph in the syntax tree. This is akin to a directory, where a NodePath is a file. May be relative or rooted, but the path always ends with a graph.
    /// </summary>
    public struct GraphPath
    {
        public LatticeGraph Root; 
        
        // todo: implement subgraphs
        // Path to a subgraph node. If this exists, it must point to a subgraph node.
        // private CodePath subgraph; 

        public int Length => 1;

        public GraphPath(LatticeGraph graph)
        {
            Root = graph;
        }

        private LatticeGraph Last => Root;
    }
}
