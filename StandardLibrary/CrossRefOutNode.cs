using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Base;
using Lattice.IR;
using Lattice.IR.Nodes;
using Unity.Entities;
using UnityEngine.Assertions;
using UnityEngine.Scripting.APIUpdating;

namespace Lattice.Nodes
{
    [MovedFrom(true, sourceAssembly:"Lattice.Runtime")]
    [NodeCreateMenu("Lattice/Utility/Reference")]
    [Serializable]
    public class CrossRefOutNode : CrossRefNode
    {
        protected override IEnumerable<PortData> GenerateInputPorts()
        {
            yield return new PortData("qualifier", optional: true)
            {
                defaultType = typeof(Entity)
            };
        }

        protected override IEnumerable<PortData> GenerateOutputPorts()
        {
            yield return new PortData("output")
            {
                defaultType = typeof(object) //GetResolvedType() // todo: circular reference via Graph.Initialize.
            };
        }

        public override void CompileToIR(IRGraph compilation)
        {
            if (!ResolvedNode.HasValue)
            {
                if (IsDisconnected()) {
                    compilation.AddNode(Path, new MalformedIRNode($"Couldn't find target node [{string.Join("/",NodePath)}] with port [{OtherPort}] on graph [{OtherGraph?.name ?? "Invalid graph ref"}]."));
                }
                else
                {
                    compilation.AddNode(Path, new MalformedIRNode("No node selected in Reference node."));
                }
                return;
            }
            
            if (ResolvedNode.Value.Last.OutputPorts.All(p => p.portData.identifier != OtherPort))
            {
                compilation.AddNode(Path, new MalformedIRNode($"Port [{OtherPort}] not found on target node."));
                return;
            }

            var crossRefNode = compilation.AddNode(Path, new LateBindingIRNode(ResolvedNode.Value, OtherPort));

            if (GetPort("qualifier").GetEdges().Count == 0) { 
                // If the cross ref has no node connected on the 'qualifier' port, then this just passed the value
                // through. This is a little odd, but we currently allow it for simplicity. If it's causing issues, 
                // it would be sensible to require the qualifier port on the CrossRefOut node.
                compilation.MapInputPort(Path, "qualifier", null);
                compilation.SetPrimaryNode(Path, crossRefNode);
                compilation.MapOutputPort(Path, "output", crossRefNode);
                return;
            }
            
            var transform = compilation.AddNode(Path, new QualifierTransformIRNode());
            
            // The 'qualifier' input is provided by the default graph compilation input connection pass.
            transform.AddInput(QualifierTransformIRNode.PortInput, crossRefNode);
            
            compilation.SetPrimaryNode(Path, transform);
            
            compilation.MapInputPort(Path, "qualifier", transform, QualifierTransformIRNode.PortQualifier);
            compilation.MapOutputPort(Path, "output", transform);
            
        }
    }
}
