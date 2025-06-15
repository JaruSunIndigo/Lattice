using System;
using System.Collections.Generic;
using Lattice.Base;
using Lattice.IR;
using Lattice.StandardLibrary;
using Lattice.Utils;
using Unity.Assertions;
using Unity.Entities;
using UnityEngine;

[assembly: RegisterGenericComponentType(typeof(ChildEntityReference.BakeDataBuffer))]

namespace Lattice.StandardLibrary
{
    /// <summary>
    ///     Returns an entity reference to the child with the given Gameobject name. The entity reference is baked at
    ///     compile-time.
    /// </summary>
    [NodeCreateMenu("Lattice/Utility/Child Entity Reference")]
    [Serializable]
    public class ChildEntityReference : BakeDataLatticeNode<Entity, ChildEntityReference>
    {
        public string GameObjectName;

        [Setting]
        public bool SearchChildren = true;

        protected override IEnumerable<PortData> GenerateInputPorts()
        {
            // Adds/removes the Disabled component.
            yield return new PortData("enabled", optional: true, defaultType: typeof(bool));
        }

        protected override IEnumerable<PortData> GenerateOutputPorts()
        {
            yield return new PortData("prefabEntity");
        }

        protected override Entity? BakeData(IBaker baker, LatticeExecutorAuthoring authoring)
        {
            Transform child = SearchChildren ? GetChildWithName(authoring.gameObject, GameObjectName) : authoring.transform.Find(GameObjectName);
            Assert.IsNotNull(child, $"Could not find child named [{GameObjectName}] on parent [{authoring.gameObject.GetPathString()}].");
            return baker.GetEntity(child, TransformUsageFlags.Dynamic);
        }

        private Transform GetChildWithName(GameObject go, string childName)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(includeInactive:true))
            {
                if (t.gameObject == go)
                {
                    // Don't return the parent if the name matches.
                    continue;
                }
                
                if (t.name == childName)
                {
                    return t;
                }
            }
            
            return null;
        }

        public override void CompileToIR(IRGraph compilation)
        {
            base.CompileToIR(compilation);

            var bakedEntity = compilation.GetNodesUnderPath(Path)[0];
            compilation.SetPrimaryNode(Path, bakedEntity);
            compilation.MapOutputPort(Path, "prefabEntity", bakedEntity);

            // Add enabling / disabling.
            if (GetPort("enabled").GetEdges().Count > 0)
            {
                var enableNode = compilation.AddNode(Path,
                    FunctionIRNode.FromStaticMethod<ChildEntityReference>(nameof(SetEnabled)));
                enableNode.AddInput("entity", bakedEntity);
                compilation.MapInputPort(Path, "enabled", enableNode, "enabled");
            }
            else
            {
                compilation.MapInputPort(Path, "enabled", null);
            }
            
        }

        public static void SetEnabled(EntityManager em, Entity entity, bool enabled)
        {
            em.SetEnabled(entity, enabled);
        }
    }
}
