using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Entities;

namespace Lattice.IR
{
    /// <summary>
    ///     This node has a single port, but accepts multiple inputs on it. All connected inputs must be of the same type.
    ///     It returns a T[] with the values. We automatically convert this to a NativeArray<T> when passing to functions.
    /// </summary>
    public class CollectIRNode<T> : CollectIRNode where T : struct
    {
        public CollectIRNode()
        {
            AddPort(typeof(T), PortInputs);
            CheckExceptions = false;
            Pure = true;
        }
        
        public override Type CalculateType(List<(string port, Type type)> valueTuples)
        {
            if (valueTuples.Any(t => t.type != typeof(T) && t.type != typeof(Nullable<>).MakeGenericType(typeof(T))))
            {
                if (valueTuples.Any(t => typeof(Exception).IsAssignableFrom(t.type)))
                {
                    return valueTuples.Count == 1
                        ? valueTuples[0].type
                        : typeof(Exception); // This could be the shared parent of the types, but for now Exception is fine.
                }
                throw new Exception(
                    $"ICE: CollectIRNode[{typeof(T)}] was passed inputs with invalid types. Input types: [{string.Join(",", valueTuples.Select(t => t.type))}]");
            }

            return typeof(List<T>);
        }

    }

    public abstract class CollectIRNode : IRNode
    {
        public const string PortInputs = "inputs";
    }
}
