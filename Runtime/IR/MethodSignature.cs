using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using JetBrains.Annotations;

namespace Lattice.IR
{
    /// <summary>
    ///     Stores the complete signature for a fully qualified public static method generated with IL. We use this to
    ///     store references to methods before they're finished being actually created. This is a useful simplification as it
    ///     allows us to not worry about whether a MethodInfo is resolvable at any given point, which allows us to code gen
    ///     methods at any point in the compilation process.
    /// </summary>
    public struct MethodSignature
    {
        // This largely mirrors MethodInfo
        
        public TypeBuilder DeclaringType;
        public string Name;
        public (Type Type, string Name)[] Parameters;
        public Type ReturnType;

        // We don't support generics, but we still use them to deduplicate created methods.
        public Type[] GenericTypeArguments;

        public MethodInfo Resolved;

        /// <summary>Resolve the concrete method this signature points at. This allows us to find it lazily.</summary>
        /// <returns>Null if not found.</returns>
        [CanBeNull]
        public MethodInfo Resolve()
        {
            try
            {
                return DeclaringType.GetTypeInfo().GetMethod(Name, Parameters.Select(p => p.Type).ToArray());
            }
            catch (AmbiguousMatchException e)
            {
                throw new Exception($"Found more than one method for signature: [{this}]", e);
            }
        }
        
        public MethodBuilder DefineMethod(TypeBuilder builder)
        {
            var method = builder.DefineMethod(Name, MethodAttributes.Public | MethodAttributes.Static, ReturnType,
                Parameters.Select(p => p.Type).ToArray());

            for (int i = 0; i < Parameters.Length; i++)
            {
                // 0 is the return type, so +1.
                method.DefineParameter(i + 1, ParameterAttributes.None, Parameters[i].Name);
            }

            return method;
        }

        public bool Equals(MethodSignature other)
        {
            return DeclaringType.Equals(other.DeclaringType) && Name == other.Name &&
                   Parameters.SequenceEqual(other.Parameters) && ReturnType == other.ReturnType &&
                   GenericTypeArguments.SequenceEqual(other.GenericTypeArguments);
        }

        public override bool Equals(object obj)
        {
            return obj is MethodSignature other && Equals(other);
        }

        public override int GetHashCode()
        {
            int h = HashCode.Combine(DeclaringType, Name, ReturnType);
            foreach ((Type Type, string Name) p in Parameters)
            {
                h = HashCode.Combine(h, p);
            }
            foreach (Type g in GenericTypeArguments)
            {
                h = HashCode.Combine(h, g);
            }
            return h;
        }

        public override string ToString()
        {
            return
                $"{DeclaringType}.{Name}<{string.Join(",", GenericTypeArguments.Select(g => g.Name))}>({string.Join(",", Parameters.Select(p => $"{p.Type.Name} {p.Name}"))})->{ReturnType.Name}";
        }
    }
}
