using System;
using System.Collections.Generic;
using Unity.Entities;

namespace Lattice.IR
{
    // The EntityIRNode can only exist in our top level graph. It's not allowed in normal graphs, because that may result 
    // in duplication of the same qualifier.
    
    /// <summary>
    ///     Built-in node that returns the Entity value of the current graph (the current lattice we're under). This is
    ///     like the 'for' node in the research version of Lattice.
    /// </summary>
    public class EntityIRNode : IRNode
    {
        public readonly Qualifier QualifierId;

        public EntityIRNode(Qualifier qualifier)
        {
            QualifierId = qualifier;
            CheckExceptions = false;
            Pure = true;
        }
        
        public override Type CalculateType(List<(string port, Type type)> valueTuples)
        {
            return typeof(Entity);
        }
    }
}
