using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Lattice.Nodes;
using Lattice.Utils;
using Unity.Assertions;
using Unity.Entities;

namespace Lattice.IR
{
    /// <summary>
    ///     This is the most common IR node, and is responsible for executing most of the actual work in a Lattice graph.
    ///     It wraps a static C# function and has input ports for each of the functions inputs. FunctionIRNode represents a
    ///     pure, blackbox c# function.  However, the specific C# function may be generated, unnameable, or unknown until code
    ///     generation time.
    /// </summary>
    public class FunctionIRNode : IRNode
    {
        public MethodInfo Method;

        /// <summary>
        ///     The return type of the blackbox function that this node represents. Usually equal to Method.ReturnValue, but
        ///     this is set earlier for some nodes that do code-gen, before Method is available.
        /// </summary>
        protected Type DefaultReturnType;

        /// <summary>
        ///     If the node has been lifted into nullability, it will automatically return null if any non-nullable input
        ///     ports are passed 'null'. These are the ports that, if passed null, will cause the node to return null.
        /// </summary>
        public List<string> NullableLiftedPorts = new();
        
        protected FunctionIRNode() { }
        
        public override IRNode MemberwiseCloneFresh()
        {
            FunctionIRNode n = (FunctionIRNode)base.MemberwiseCloneFresh();
            n.NullableLiftedPorts = new List<string>(n.NullableLiftedPorts);
            return n;
        }


        /// <summary>Creates a new FunctionIRNode wrapping the given C# function.</summary>
        public FunctionIRNode(MethodInfo method)
        {
            if (method == null)
            {
                throw new Exception("FunctionIRNode created with null method.");
            }

            DebugName = method.Name;

            // Also does validation.
            SetMethod(method);

            // Add ports for each parameter
            foreach (var param in Method.GetParameters())
            {
                if (param.ParameterType == typeof(LatticeNode) || param.ParameterType == typeof(EntityManager))
                {
                    continue; // Don't create ports for these types. They are passed implicitly.
                }

                Type portType = param.ParameterType;

                if (param.ParameterType.IsByRef)
                {
                    if (param.IsRefParameter())
                    {
                        // 'ref' parameters take the direct pointer type as input.
                        portType = param.ParameterType;
                    }
                    else
                    {
                        // 'in' parameters just take the concrete value. They're passed as a temporary ref on execution. 
                        portType = param.ParameterType.GetElementType();
                    }
                }

                AddPort(portType, param.Name, optional: param.IsOptional);
            }
        }

        /// <summary>Creates a FunctionIRNode that runs the given static method when it executes.</summary>
        public static FunctionIRNode Create(MethodInfo method)
        {
            return new FunctionIRNode(method);
        }

        /// <summary>
        ///     Creates a FunctionIRNode that runs the given static method when it executes. This overload can be passed a
        ///     MethodGroup.
        /// </summary>
        public static FunctionIRNode Create<TResult>(Action<TResult> function)
        {
            return new FunctionIRNode(function.Method);
        }

        /// <summary>
        ///     Creates a FunctionIRNode that runs the given static method when it executes. This overload can be passed a
        ///     MethodGroup.
        /// </summary>
        public static FunctionIRNode Create<TParam0, TResult>(Func<TParam0, TResult> function)
        {
            return new FunctionIRNode(function.Method);
        }

        /// <summary>
        ///     Creates a new FunctionIRNode that executes the given static method on the given class. Recommended to use
        ///     nameof() for ergonomics.
        /// </summary>
        /// <param name="methodName">The name of the method.</param>
        /// <typeparam name="T">The declaring class type of the method.</typeparam>
        public static FunctionIRNode FromStaticMethod<T>(string methodName)
        {
            MethodInfo methodInfo = typeof(T).GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (methodInfo!.IsGenericMethodDefinition)
            {
                throw new Exception(
                    $"Function passed to FromStaticMethod was a generic definition. You must pass the concrete type of the function you want. [{methodInfo.Name}]");
            }

            return new FunctionIRNode(methodInfo);
        }

        /// <summary>
        ///     Creates a new FunctionIRNode that executes the given static method. Recommended to use nameof() for
        ///     ergonomics.
        /// </summary>
        /// <param name="methodName">Name of the static method. Use nameof.</param>
        /// <param name="genericParameters">Values for any generic parameters.</param>
        /// <typeparam name="T">The declaring type.</typeparam>
        public static FunctionIRNode FromStaticMethod<T>(string methodName, params Type[] genericParameters)
        {
            MethodInfo method = typeof(T).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);

            if (method == null)
            {
                method = typeof(T).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            }

            if (method == null)
            {
                throw new Exception($"Couldn't find static method [{methodName}] on type [{typeof(T)}]");
            }

            if (genericParameters == null)
            {
                throw new Exception($"Tried to create generic method [{methodName}] with a null type list.");
            }

            foreach (var p in genericParameters)
            {
                if (p == null)
                {
                    throw new Exception(
                        $"Tried to create generic method [{methodName}] with a null type parameter. [{string.Join(",", genericParameters.Select(p => p?.FullName))}]");
                }

                if (p.IsByRef)
                {
                    throw new Exception($"Cannot create generic method with ref type [{p.Name}] as generic.");
                }

                if (p == typeof(void))
                {
                    throw new Exception("Cannot use System.Void as a generic type parameter for a method.");
                }
            }

            if (genericParameters.Length > 0)
            {
                method = method.MakeGenericMethod(genericParameters);
            }

            return new FunctionIRNode(method);
        }

        /// <summary>
        ///     Sets the underlying MethodInfo that this node will use for code generation. Does not update the Ports. This is
        ///     usually set in the constructor, but some nodes (like those that do code-gen) set this later in the compilation.
        /// </summary>
        public void SetMethod(MethodInfo info)
        {
            
            if (info.IsStatic == false)
            {
                // We only support functions that are fully static methods. Lambda functions will not work, even if 
                // they are declared 'static' because the compiler still generates them as an instance method on a 
                // dummy class.
                throw new Exception(
                    $"Function passed to FunctionIRNode must be static. It may not be a lambda or instance method. [{info.Name}]");
            }

            if (info.IsGenericMethod)
            {
                foreach (var t in info.GetGenericArguments())
                {
                    if (t.IsByRef)
                    {
                        throw new Exception(
                            $"Cannot create FunctionIRNode with method with ref type [{t.Name}] as generic.");
                    }
                }
            }

            foreach (var param in info.GetParameters())
            {
                if (param.ParameterType == typeof(EntityManager))
                {
                    MustRunOnMainThread = true;
                }
                
                if (param.IsOut)
                {
                    throw new Exception(
                        $"Lattice does not support functions with 'out' parameters. [{info.Name}]");
                }

                if (param.IsIn)
                {
                    throw new Exception(
                        $"Lattice does not support functions with 'in' parameters. [{info.Name}]");
                }

                if (string.IsNullOrEmpty(param.Name))
                {
                    throw new Exception(
                        "Method passed to FunctionIRNode does not have names for all parameters. " +
                        $"Param [{param.Position}] is missing name on method [{info.DeclaringType}.{info.Name}]. " +
                        "Did you generate this method with Reflection.Emit and forget to define the parameters with MethodBuilder?");
                }
            }

            Method = info;
            DefaultReturnType = Method.ReturnType == typeof(void) ? typeof(Unit) : Method.ReturnType;
        }

        /// <summary>
        ///     Create ports on this node based on the given method signature. Useful if you don't have a valid MethodInfo
        ///     yet.
        /// </summary>
        protected void CreatePorts(MethodSignature signature)
        {
            foreach (var (type, name) in signature.Parameters)
            {
                if (type == typeof(LatticeNode) || type == typeof(EntityManager))
                {
                    continue; // Don't create ports for these types. They are passed implicitly.
                }
                
                Assert.IsFalse(type.IsByRef, "Pointers can't be ports.");
                AddPort(type, name);
            }

            DefaultReturnType = signature.ReturnType == typeof(void) ? typeof(Unit) : signature.ReturnType;
        }

        public override Type CalculateType(List<(string port, Type type)> inputTypes)
        {
            EvaluateNullableLifting(inputTypes);

            Type outputType = DefaultReturnType;
            Assert.IsNotNull(outputType, "No output type was set for FunctionIRNode.");

            if (NullableLiftedPorts is { Count: > 0 } && !outputType.IsNullable())
            {
                Assert.IsTrue(outputType.IsValueType, "ICE: Only value types can be nullable lifted.");
                outputType = typeof(Nullable<>).MakeGenericType(outputType);
            }

            return outputType;
        }

        // Calculate nullable lifting. 
        // This compares the input type provided to the ports with the input types to the function.
        // If the input provided is the Nullable form of the param, we lift this node into nullability. 
        protected void EvaluateNullableLifting(List<(string port, Type type)> inputTypes)
        {
            NullableLiftedPorts?.Clear();
            
            foreach (var (port, inputType) in inputTypes)
            {
                if (inputType.IsGenericType &&
                    inputType.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    Type innerInputType = inputType.GetGenericArguments()[0];
                    if (Ports[port].Type == innerInputType)
                    {
                        NullableLiftedPorts ??= new List<string>();
                        NullableLiftedPorts.Add(port);
                    }
                }
            }
        }
    }

    /// <summary>A set of basic FunctionIRNodes that we use frequently.</summary>
    public static class CoreIRNodes
    {
        /// <summary>A FunctionIRNode that just returns its input. Useful for rerouting values, or generally for organization.</summary>
        public static FunctionIRNode Identity(Type t)
        {
            var node = new FunctionIRNode(
                typeof(CoreIRNodes).GetMethod(nameof(IdentityFunc), BindingFlags.Public | BindingFlags.Static)!
                                   .MakeGenericMethod(t))
            {
                CheckExceptions = false, // An identity node can never throw an exception.
                Pure = true, // An identity node is always pure.
            };
            return node;
        }
        
        public static FunctionIRNode CopyState(Type t)
        {
            var node = new FunctionIRNode(
                typeof(CoreIRNodes).GetMethod(nameof(CopyStateFunc), BindingFlags.Public | BindingFlags.Static)!
                                   .MakeGenericMethod(t))
            {
                CheckExceptions = false // A copy node can never throw an exception.
            };
            return node;
        }

        public static T IdentityFunc<T>(T value)
        {
            return value;
        }
        
        public static T CopyStateFunc<T>(LatticeState.Wrapper<T> value)
        {
            return value.Obj;
        }
        
        public static T DefaultValue<T>()
        {
            return default;
        }
    }

    // An exception that came from an above input, rather than this node.
    public class InputNodeException : Exception
    {
        public IRNode SourceNode;

        public InputNodeException(IRNode source, Exception inner) : base($"Error in input node [{source}]", inner)
        {
            Assert.IsFalse(inner is InputNodeException);
            SourceNode = source;
        }

        public override string ToString()
        {
            StringBuilder b = new StringBuilder();

            // Use newlines between exception types.
            Exception e = this;
            while (e != null)
            {
                // if last
                if (e.InnerException == null)
                {
                    b.Append(e);
                    e = e.InnerException;
                }
                else
                {
                    b.Append(e.GetType().Name);
                    b.Append(": ");
                    b.Append(e.Message);
                    b.Append("\n");
                    e = e.InnerException;
                }
            }

            return b.ToString();
        }
    }
}
