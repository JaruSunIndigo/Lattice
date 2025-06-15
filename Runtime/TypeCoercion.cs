// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Reflection;
// using JetBrains.Annotations;
//
// namespace Lattice
// {
//     // John: Type coercion adapters are disabled for now, because the TypeCache is not accessible at Runtime.
//     // We'll want to write a simpler way to register type coercions, perhaps from a static constructor.
//
//     // I've left most of the code here so it's still hooked into execution for when we replace it.
//
//     /// <summary>
//     ///     Implement this interface to use the inside your class to define type convertions to use inside the graph. Example:
//     ///     <code>
//     /// public class CustomConvertions : ITypeAdapter
//     /// {
//     ///     public static Vector4 ConvertFloatToVector(float from) => new Vector4(from, from, from, from);
//     ///     ...
//     /// }
//     /// </code>
//     /// </summary>
//     /// <remarks>Use public static methods for automatic conversion.</remarks>
//     // public abstract class ITypeCoercion // TODO: turn this back into an interface when we have C# 8
//     // {
//     //     public virtual IEnumerable<(Type, Type)> GetIncompatibleTypes()
//     //     {
//     //         yield break;
//     //     }
//     // }
//
//     /// <summary>
//     ///     Stores function references for converting types between other types in the graph and determining if two types
//     ///     are compatible.
//     /// </summary>
//     public static class TypeCoercion
//     {
//         /// <summary>Functions used to automatically coerce types to other types.</summary>
//         private static readonly Dictionary<(Type from, Type to), Func<object, object>> TypeCoercions = new();
//
//         /// <summary>
//         ///     Manual overrides for types that should always be incompatible in the graph (even if the C# types are
//         ///     compatible).
//         /// </summary>
//         private static readonly List<(Type from, Type to)> IncompatibleTypes = new();
//
//         private static bool initialized;
//
//         /// <summary>Used with reflection below. Converts a MethodInfo into a Func[object,object] pointer for performance.</summary>
//         [UsedImplicitly]
//         private static Func<object, object> ConvertTypeMethodHelper<TParam, TReturn>(MethodInfo method)
//         {
//             // Convert the slow MethodInfo into a fast, strongly typed, open delegate
//             var func = (Func<TParam, TReturn>)Delegate.CreateDelegate(typeof(Func<TParam, TReturn>), method);
//
//             // Now create a more weakly typed delegate which will call the strongly typed one
//             return param => func((TParam)param);
//         }
//
//         /// <summary>Skims the loaded assemblies and pulls out all custom type coercions. Initialized only once.</summary>
//         private static void LoadAllAdapters()
//         {
//             // John: Type coercion adapters are disabled for now, because the TypeCache is not accessible at Runtime.
//             // We'll want to write a simpler way to register type coercions, perhaps from a static constructor.
//
//             // foreach (Type type in TypeCache.GetTypesDerivedFrom<ITypeCoercion>())
//             // {
//             //     if (type.IsAbstract)
//             //     {
//             //         continue;
//             //     }
//             //
//             //     if (Activator.CreateInstance(type) is ITypeCoercion adapter)
//             //     {
//             //         foreach (var types in adapter.GetIncompatibleTypes())
//             //         {
//             //             IncompatibleTypes.Add((types.Item1, types.Item2));
//             //             IncompatibleTypes.Add((types.Item2, types.Item1));
//             //         }
//             //     }
//             //
//             //     //
//             //     foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public |
//             //                                            BindingFlags.NonPublic))
//             //     {
//             //         if (method.GetParameters().Length != 1)
//             //         {
//             //             Debug.LogError(
//             //                 $"Ignoring coercion method {method} because it does not have exactly one parameter");
//             //             continue;
//             //         }
//             //         if (method.ReturnType == typeof(void))
//             //         {
//             //             Debug.LogError($"Ignoring coercion method {method} because it does not return anything");
//             //             continue;
//             //         }
//             //
//             //         Type from = method.GetParameters()[0].ParameterType;
//             //         Type to = method.ReturnType;
//             //
//             //         try
//             //         {
//             //             // Create a faster object->object delegate from the reflected method.
//             //             MethodInfo genericHelper = typeof(TypeCoercion).GetMethod(nameof(ConvertTypeMethodHelper),
//             //                 BindingFlags.Static | BindingFlags.NonPublic);
//             //             // Now supply the type arguments
//             //             MethodInfo constructedHelper = genericHelper.MakeGenericMethod(from, to);
//             //             var r = (Func<object, object>)constructedHelper.Invoke(null, new object[] { method });
//             //
//             //             // Store it in our lookup dictionary for the future.
//             //             TypeCoercions.Add((method.GetParameters()[0].ParameterType, method.ReturnType), r);
//             //         }
//             //         catch (Exception e)
//             //         {
//             //             Debug.LogError($"Failed to load the type coercion method: {method}\n{e}");
//             //         }
//             //     }
//             // }
//             //
//             // // Ensure that the dictionary contains all the coercions in both ways
//             // // ex: float to vector but no vector to float
//             // foreach (var kp in TypeCoercions)
//             // {
//             //     if (!TypeCoercions.ContainsKey((kp.Key.to, kp.Key.from)))
//             //     {
//             //         Debug.LogError(
//             //             $"Missing convertion method. There is one for {kp.Key.from} to {kp.Key.to} but not for {kp.Key.to} to {kp.Key.from}");
//             //     }
//             // }
//
//             initialized = true;
//         }
//
//         /// <summary>
//         ///     Is it possible to coerce between these two types? (Two of the same types are always coerceable unless
//         ///     explicitly denied via an IncompatibleType annotation).
//         /// </summary>
//         public static bool AreAssignable(Type from, Type to)
//         {
//             if (!initialized)
//             {
//                 LoadAllAdapters();
//             }
//
//             bool incompatible = IncompatibleTypes.Any(k => k.from == from && k.to == to);
//
//             return !incompatible && TypeCoercions.ContainsKey((from, to));
//         }
//
//         public static object Coerce(object from, Type targetType)
//         {
//             if (!initialized)
//             {
//                 LoadAllAdapters();
//             }
//
//             if (TypeCoercions.TryGetValue((from.GetType(), targetType), out Func<object, object> convertionFunction))
//             {
//                 return convertionFunction?.Invoke(from);
//             }
//
//             return null;
//         }
//     }
// }
