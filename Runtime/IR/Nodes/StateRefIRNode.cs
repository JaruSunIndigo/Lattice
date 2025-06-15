using System;
using System.Collections.Generic;

namespace Lattice.IR
{
    
    // todo: Currently the StateRefIRNode doesn't add an input edge to the EntityIRNode. This is functional, because
    // it absorbs the Lattice Qualifier from the MutatorIRNode after analysis. However, it's not *quite* correct, 
    // because technically it should need to be calculated *after* the entity 'for' node. This would be important
    // if we ever implement dynamic for nodes, or multiple qualifiers.
    
    /// <summary>
    ///     Similar to <see cref="PreviousIRNode" /> except that it returns a pointer to the allocated state value, rather
    ///     than the struct value. This node cannot be analyzed at the higher-level IR but is much more efficient for
    ///     execution.
    /// </summary>
    public class StateRefIRNode : IRNode
    {
        public const string DefaultValuePort = "default";

        public StateRefIRNode(Type stateType)
        {
            ExecutionOnly = true;

            AddPort(stateType, DefaultValuePort);
        }

        // Unless we have a lower/higher split, we don't have an implementation for this because lower nodes cannot be analyzed. 
        public override Type CalculateType(List<(string port, Type type)> valueTuples) =>
            throw new NotImplementedException(
                "This node is only added during lowering, so this calculation should never be called.");
    }
}
