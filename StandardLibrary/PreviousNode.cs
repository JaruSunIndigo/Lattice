using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Base;
using Lattice.IR;
using Lattice.IR.Nodes;
using Lattice.Nodes;
using Unity.Assertions;
using Unity.Entities;
using UnityEngine;

namespace Lattice.StandardLibrary
{
    [NodeCreateMenu("Lattice/Utility/Previous Value")]
    public class PreviousNode : CrossRefNode
    {
        protected override IEnumerable<PortData> GenerateInputPorts()
        {
            yield return new PortData("default", optional: true);
        }

        protected override IEnumerable<PortData> GenerateOutputPorts()
        {
            yield return new PortData("previous");
        }
        public override void CompileToIR(IRGraph compilation)
        {
            if (!ResolvedNode.HasValue) {
                compilation.AddNode(Path, new MalformedIRNode("No node selected."));
                return;
            }
            
            if (ResolvedNode.Value.Last.OutputPorts.All(p => p.portData.identifier != OtherPort))
            {
                compilation.AddNode(Path, new MalformedIRNode($"Port [{OtherPort}] not found on target node."));
                return;
            }

            var crossRefNode = compilation.AddNode(Path, new LateBindingIRNode(ResolvedNode.Value, OtherPort));
            
            var prevNode = compilation.AddNode(Path, new PreviousIRNode());
            prevNode.BackRef = compilation.GetNodeRef(crossRefNode);
            
            if (!GetPort("default").GetEdges().Any()) {
                // Hmm. We need it to be the inferred type! Which.. means we need generics gah.
                // compilation.AddNode(this, FunctionIRNode.FromStaticMethod<PreviousNode>(nameof(GetDefaultValue), typeof()) );
                throw new NotImplementedException("Unfinished work on previous ir node.");
            }
        }
        
        public static T GetDefaultValue<T>()
        {
            return default;
        }

    }
}
