namespace Lattice.IR
{
    /// <summary>
    ///     A layer of indirection to make it easier to replace nodes in the graph without chasing down all the external
    ///     pointers into the graph and updating them. Allows you to redirect all pointers to a node to a new node.
    /// </summary>
    public class IRNodeRef
    {
        public IRNode Node;
        public int RefCount;
    }
}
