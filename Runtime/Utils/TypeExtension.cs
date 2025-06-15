using System;
using System.Reflection;

namespace Lattice.Utils
{
    public static class TypeExtension
    {
        /// <summary>Returns if the type is of the form Nullable<TSomeOtherType></summary>
        public static bool IsNullable(this Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
        }

        /// <summary>Returns if the parameter is declared with the 'ref' keyword.</summary>
        public static bool IsRefParameter(this ParameterInfo param)
        {
            // in and out params are also 'by ref' in the sense that they are reference types. So ref keywords are
            // any params that are references that are not in or out. 
            // https://stackoverflow.com/questions/1551761/ref-parameters-and-reflection
            return param.ParameterType.IsByRef && !param.IsIn && !param.IsOut;
        }
    }
}
