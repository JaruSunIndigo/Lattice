using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Assertions;
using Unity.Entities;

namespace Lattice.IR
{
    /// <summary>Calculates the node passed to port 0, under the qualifier provided by the second port.</summary>
    public class QualifierTransformIRNode : IRNode
    {
        public const string PortInput = "input";
        public const string PortQualifier = "qualifier";
        
        public QualifierTransformIRNode()
        {
            AddPort(typeof(object), PortInput);
            AddPort(typeof(Entity), PortQualifier);
            
            // QTransforms must check exceptions, because we throw if the entity reference is invalid.
            CheckExceptions = true;
            Pure = true; 
        }

        public override Type CalculateType(List<(string port, Type type)> inputTypes)
        {
            // Pass through input type from the first port.
            var (portId, firstInputType) = inputTypes.First();
            Assert.AreEqual(PortInput, portId);

            return firstInputType;
        }
    }
}
