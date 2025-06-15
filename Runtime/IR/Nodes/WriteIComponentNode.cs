using System;
using System.Reflection;
using GrEmit;
using Unity.Entities;

namespace Lattice.IR.Nodes
{
    // Generates the following code, but without needing reflection!
    //  void WriteIComponentField(EntityManager em, Entity entity, {FieldType} value) {
    //      object currentValue = manager.GetComponentData<T>(e);
    //      field.SetValue(currentValue, fieldData);
    //      manager.SetComponentData(e, (T)currentValue);
    //  }
    /// <summary>Generates a static method that sets the given field on the ECS Component.</summary>
    public class WriteIComponentNode : FunctionIRNode
    {
        public readonly FieldInfo Field;

        public WriteIComponentNode(FieldInfo field)
        {
            Field = field;

            CreatePorts(GetSignature());
        }

        private MethodSignature GetSignature() {
            return new MethodSignature
            {
                Name = $"WriteIComponentField_{Field.DeclaringType!.Namespace}.{Field.DeclaringType.Name}.{Field.Name}",
                                ReturnType = typeof(void),
                Parameters = new[]
                {
                    (typeof(EntityManager), "entityManager"), (typeof(Entity), "entity"), (Field.FieldType, "value")
                }
            };

        }

        // public override IRNode Clone()
        // {
        //     return new WriteIComponentNode(Field);
        // }

        public static void CodeGen(GraphCompilation compilation)
        {
            compilation.DeduplicatedCodeGen<FieldInfo, WriteIComponentNode>("WriteIComponentFields",
                node => node.Field,
                (node, typebuilder) =>
                {
                    var method = node.GetSignature().DefineMethod(typebuilder);

                    // Load the arguments and get the component struct from EntityManager with Entity.
                    GroboIL emit = new(method);

                    emit.Ldarga(0); // Load EntityManager onto the stack
                    emit.Ldarg(1); // Load Entity onto the stack
                    emit.Call(
                        typeof(EntityManager).GetMethod(nameof(EntityManager.GetComponentData),
                                                 new[] { typeof(Entity) })!
                                             .MakeGenericMethod(node.Field.DeclaringType));

                    // Write to the field.
                    var componentLocal = emit.DeclareLocal(node.Field.DeclaringType, "component");
                    emit.Stloc(componentLocal); // Store return value in local. 
                    emit.Ldloca(componentLocal); // Load a pointer to the local, so we can set it.
                    emit.Ldarg(2); // Load field value.
                    emit.Stfld(node.Field); // Write the value into the field.

                    // Set the value back to ECS.
                    emit.Ldarga(0); // Load EntityManager onto the stack
                    emit.Ldarg(1); // Load Entity onto the stack
                    emit.Ldloc(componentLocal); // Load the modified component.
                    emit.Call(
                        typeof(EntityManager).GetMethod(nameof(EntityManager.SetComponentData),
                            new[] { typeof(Entity), Type.MakeGenericMethodParameter(0) })!.MakeGenericMethod(
                            node.Field.DeclaringType));

                    // no return value.
                    emit.Ret();

                    return method;
                });
        }
    }
}
