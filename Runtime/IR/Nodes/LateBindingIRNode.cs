using System;
using System.Collections.Generic;

namespace Lattice.IR.Nodes
{
    /// <summary>
    /// This node only exists during graph building, and *before* analysis. It serves as a reference to another node
    /// potentially in another syntax IR Graph. Think of this like a symbol reference between files or compilation units.
    /// It is removed during compilation after the full graph is built.
    /// </summary>
    public class LateBindingIRNode : IRNode
    {
        public CodePath BindingPath;
        public string OutputPort;

        public LateBindingIRNode(CodePath path, string outputPort)
        {
            CannotExecute = true;
            BindingPath = path;
            OutputPort = outputPort;

            // todo: We should convert ExecutionOnly to a set of flags for which level of the IR each node participates at.
            // That would allow banning this node from analysis, because we have three layers:
            // Graph setup nodes
            // Analysis nodes
            // Lowered Execution nodes
        }

        public override Type CalculateType(List<(string port, Type type)> valueTuples)
        {
            throw new NotImplementedException("LateBindingIRNode should be removed before analysis of the graph.");
        }

        public override IRNode MemberwiseCloneFresh()
        {
            var n = (LateBindingIRNode) base.MemberwiseCloneFresh();
            n.BindingPath = BindingPath;
            return n;
        }
    }
}
