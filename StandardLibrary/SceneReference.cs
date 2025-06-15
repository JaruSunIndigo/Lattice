using System;
using System.Collections.Generic;
using Lattice.Base;
using Lattice.IR;
using Lattice.StandardLibrary;
using Unity.Entities;
using UnityEngine;

[assembly: RegisterGenericComponentType(typeof(FindEntity.BakeDataBuffer))]

namespace Lattice.StandardLibrary
{
    [NodeCreateMenu("Lattice/Utility/Find Entity At Bake Time")]
    [Serializable]
    public class FindEntity : BakeDataLatticeNode<Entity, FindEntity>
    {
        public string GameObjectName;

        public override string DefaultName => "Find Entity in Subscene";

        protected override IEnumerable<PortData> GenerateOutputPorts()
        {
            yield return new PortData("PrefabEntity");
        }

        /// <inheritdoc />
        public override void CompileToIR(IRGraph compilation)
        {
            base.CompileToIR(compilation);
            
            // We need to set this here for now. We could set it in the base, but that would break other implementors
            // because we aren't allowed to set primary node once it's set.
            compilation.SetPrimaryNode(Path, compilation.GetNodesUnderPath(Path)[0]);
        }

        protected override Entity? BakeData(IBaker baker, LatticeExecutorAuthoring authoring)
        {
            // Bake the prefab to entities and store the reference onto the graph's entity.
            GameObject[] rootsInScene = authoring.gameObject.scene.GetRootGameObjects();
            Transform found = null;
            foreach (var root in rootsInScene)
            {
                if (root.name == GameObjectName)
                {
                    found = root.transform;
                    break;
                }

                found = root.transform.Find(GameObjectName);
            }
            
            if (found == null)
            {
                Debug.LogError($"Scene reference [{GameObjectName}] could not be found in Scene [{authoring.gameObject.scene.path}]. [{this}]");
                return null;
            }

            return baker.GetEntity(found, TransformUsageFlags.Dynamic);
        }

    }
}
