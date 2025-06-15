using System;
using System.Collections.Generic;
using Lattice.Base;
using Lattice.IR;
using Unity.Entities;
using UnityEngine.Scripting.APIUpdating;

namespace Lattice.Nodes
{
    /// <summary>
    ///     Returns the entity given by this node's qualifiers. Must be used within the SharedContext of a script running on an
    ///     entity.
    /// </summary>
    [MovedFrom(true, sourceAssembly:"Lattice.Runtime")]
    [Serializable]
    [NodeCreateMenu("Lattice/Utility/This Entity")]
    public class EntityNode : LatticeNode
    {
        private const string PortEntity = "Output";
        
        protected override IEnumerable<PortData> GenerateOutputPorts()
        {
            yield return new PortData(PortEntity);
        }

        public override string DefaultName => "Entity";

        public override void CompileToIR(IRGraph compilation)
        {
           IRNode entityNode = compilation.GetImplicitEntity(Graph);
           
           // We use a rerouted version instead of returning the actual node so that there is an IR node within each EntityNode
           // to point at for debugging. The implicit entity is a graph node associated with no specific LatticeNode.
           FunctionIRNode reroute = compilation.AddNode(Path, "ThisEntityReroute", CoreIRNodes.Identity(typeof(Entity)));
           reroute.AddInput("value", entityNode);
           
           compilation.MapOutputPort(Path, PortEntity, reroute);
           compilation.SetPrimaryNode(Path, reroute);
        }
    }
}
