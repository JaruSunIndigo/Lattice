using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GrEmit;
using Lattice.Base;
using Lattice.Utils;
using UnityEngine;
using UnityEngine.Assertions;

namespace Lattice.IR
{
    /// <summary>A FunctionIRNode that represents a field access on a value.</summary>
    public class FieldAccessorIRNode : FunctionIRNode
    {
        public const string PortInput = "input";
        public readonly string FieldName;

        public FieldInfo Field; // Not resolved until after type inference.

        public FieldAccessorIRNode(string fieldName)
        {
            Assert.IsFalse(string.IsNullOrEmpty(fieldName));

            FieldName = fieldName;

            // A field accessor is pure, no side effects.
            Pure = true;
            DebugName = $"field_{fieldName}";

            // Null type because this can accept "any"
            AddPort(Type.MakeGenericMethodParameter(0), PortInput);
        }

        public override Type CalculateType(List<(string port, Type type)> inputTypes)
        {
            Assert.AreEqual(inputTypes.Count, 1);

            Type inputType = inputTypes[0].type;

            // There's nothing we can do if the input type can't be inferred, just pass through the error.
            if (inputType == typeof(ITypeUnknown))
            {
                return typeof(ITypeUnknown);
            }
            
            // If the input is connected to an exception node, pass that type through.
            if (typeof(Exception).IsAssignableFrom(inputType))
            {   
                return inputType;
            }

            // Fields automatically propagate nullable inputs by acting on the inner type.
            Field = inputType.IsNullable()
                ? inputType.GetGenericArguments()[0].GetField(FieldName)
                : inputType.GetField(FieldName);
            
            if ( Field == null )
            {
                throw new Exception($"ICE: Field [{FieldName}] not found on [{inputType}]. Node: [{this}]");
            }

            // Update port types now that we know the field.
            Ports[PortInput].Type = Field.DeclaringType;

            DefaultReturnType = Field.FieldType;

            // A field accessor can throw if the input value is null. However for value types this is impossible.
            CheckExceptions = !Field.DeclaringType!.IsValueType;

            return base.CalculateType(inputTypes);
        }

        public static void CodeGen(GraphCompilation compilation)
        {
            compilation.DeduplicatedCodeGen<FieldInfo, FieldAccessorIRNode>("FieldAccessors", node => node.Field,
                (node, builder) =>
                {
                    MethodBuilder method = builder.DefineMethod(
                        $"Field_{node.Field.DeclaringType!.FullName}.{node.Field.Name}",
                        MethodAttributes.Public | MethodAttributes.Static, node.Field.FieldType,
                        new[] { node.Field.DeclaringType });

                    GroboIL emit = new(method);
                    emit.Ldarg(0);
                    emit.Ldfld(node.Field);
                    emit.Ret();

                    method.DefineParameter(1, ParameterAttributes.None, PortInput); // param 0 is the return type!

                    return method;
                });
        }
    }
}