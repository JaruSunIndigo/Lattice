using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Lattice.Utils;
using Unity.Assertions;
using UnityEngine.Pool;

namespace Lattice.IR
{
    /// <summary>
    ///     This represents a function execution that mutates several of its inputs, taken by ref. This node is never
    ///     actually executed, but it represents an operation in the lattice abstract machine. Abstractly, this executes the
    ///     function on copies of the input refs, and returns a tuple with a copy of the resulting refs, alongside the original
    ///     return value of the function.
    /// </summary>
    public class MutatorFunctionIRNode : FunctionIRNode
    {
        /// <summary>The output type of this function node. (Doesn't support type inference).</summary>
        // public readonly Type TupleType;
        
        private readonly string OutputValueTupleField;

        public MutatorFunctionIRNode(MethodInfo method) : base(method)
        {
            CannotExecute = true;
            
            // Force port type to be non-ref
            foreach (var p in Method.GetParameters())
            {
                if (p.ParameterType.IsByRef)
                {
                    Ports[p.Name].Type = p.ParameterType.GetElementType();
                }
            }
        }

        public override Type CalculateType(List<(string port, Type type)> inputs)
        {
            using var _ = CollectionPool<List<Type>, Type>.Get(out List<Type> outputs);

            // Add an output in the tuple for each ref variable. This is logically the copy.
            foreach (var p in Method.GetParameters())
            {
                if (p.ParameterType.IsByRef)
                {
                    outputs.Add(p.ParameterType.GetElementType());
                }
            }
            
            EvaluateNullableLifting(inputs);

            // Handle nullable lifting, slightly different from FunctionIRNode. The 'return value' value of the output
            // tuple is nullable lifted, but not the entire output type. This is so that state does not get nulled out
            // when nullable lifted -- we want that to pass through.
            Type valueType = DefaultReturnType; 
            if (NullableLiftedPorts is { Count: > 0 } && !valueType.IsNullable())
            {
                Assert.IsTrue(valueType.IsValueType, "ICE: Only value types can be nullable lifted.");
                valueType = typeof(Nullable<>).MakeGenericType(valueType);
            }
        
            // Add the normal output of the function.
            outputs.Add(valueType);

            return CreateValueTupleType(outputs);
        }

        private static Type CreateValueTupleType(List<Type> types)
        {
            switch (types.Count)
            {
                case 1:
                    return typeof(ValueTuple<>).MakeGenericType(types.ToArray());
                case 2:
                    return typeof(ValueTuple<,>).MakeGenericType(types.ToArray());
                case 3:
                    return typeof(ValueTuple<,,>).MakeGenericType(types.ToArray());
                case 4:
                    return typeof(ValueTuple<,,,>).MakeGenericType(types.ToArray());
                case 5:
                    return typeof(ValueTuple<,,,,>).MakeGenericType(types.ToArray());
                case 6:
                    return typeof(ValueTuple<,,,,,>).MakeGenericType(types.ToArray());
                case 7:
                    return typeof(ValueTuple<,,,,,,>).MakeGenericType(types.ToArray());
                case 8:
                    return typeof(ValueTuple<,,,,,,,>).MakeGenericType(types.ToArray());
                default:
                    throw new ArgumentException("ValueTuple supports up to 8 elements.");
            }
        }
    }
}
