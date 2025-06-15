using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Lattice.Base;
using Lattice.IR;

namespace Lattice.Nodes
{
    /// <summary>An identity node used for graph organisation.</summary>
    [Serializable]
    public sealed class RedirectNode : LatticeNode
    {
        private const string PortInput = "value";
        private const string PortOutput = "output";
        
        /// <summary>Gets the type associated with the redirect node by prioritising the connected input port, and then the output port.</summary>
        [CanBeNull]
        private Type Type => FromPort?.portData.defaultType ?? ToPort?.portData.defaultType;

        [CanBeNull]
        private NodePort FromPort => InputPorts.FirstOrDefault()?.GetEdges().FirstOrDefault()?.fromPort;
        
        [CanBeNull]
        private NodePort ToPort => OutputPorts.FirstOrDefault()?.GetEdges().FirstOrDefault()?.toPort;

        public override string DefaultName => "";

        /// <inheritdoc />
        public override bool AllowsCollapsedNodesOnPorts => false;

        protected override IEnumerable<PortData> GenerateInputPorts()
        {
            yield return new PortData(PortInput)
            {
                defaultType = Type,
                vertical = true,
#if UNITY_EDITOR
                customTooltip = "",
                acceptMultipleEdges = ToPort?.portData.acceptMultipleEdges ?? false
#endif
            };
        }

        protected override IEnumerable<PortData> GenerateOutputPorts()
        {
            yield return new PortData(PortOutput)
            {
                defaultType = Type,
                vertical = true,
#if UNITY_EDITOR
                customTooltip = "",
                acceptMultipleEdges = FromPort?.portData.acceptMultipleEdges ?? true
#endif
            };
        }

        public override void CompileToIR(IRGraph compilation)
        {
            Type type = Type;
            if (type == null)
            {
                throw new LatticePortRequirementException("Port connection is required.", PortInput);
            }
            
            FunctionIRNode node = compilation.AddNode(Path, CoreIRNodes.Identity(type), "RedirectNode");
            
            LatticeNode input = (LatticeNode)InputPorts[0].GetEdges().Select(e => e.fromNode).FirstOrDefault();
            if (input == null)
            {
                throw new LatticePortRequirementException("Input port is required.", PortInput);
            }
            LatticeNode output = (LatticeNode)OutputPorts[0].GetEdges().Select(e => e.fromNode).FirstOrDefault();
            if (output == null)
            {
                throw new LatticePortRequirementException("Output port is required.", PortOutput);
            }
            
            compilation.MapInputPorts(Path, node);
            compilation.SetPrimaryNode(Path, node);
            compilation.MapOutputPort(Path, PortOutput, node);
        }
    }
}
