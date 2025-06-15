using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Lattice.IR.Nodes;
using Lattice.Utils;
using Unity.Assertions;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine.Pool;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lattice.IR
{
    // A graph compiler holds the context for a single invocation of the compilation pipeline on a set of lattice graphs.
    public static class GraphCompiler
    {
        // Safe but slow graph organization checks, like verifying usages, 
        internal const bool EnableVerboseChecks = false;

        // Controls dead code elimination pass based on pure nodes.
        private const bool EnableDeadCodeElimination = true;

        /// <summary>Configuration options for the invocation of the lattice graph compiler.</summary>
        public struct Settings : IEquatable<Settings>
        {
            /// <summary>
            ///     Specifies how to write out the generated assembly. RunAndCollect is the only valid option for execution, but
            ///     Save can be used to save an assembly for inspection. Unfortunately, currently these assemblies do not have correct
            ///     permissions to be loaded.
            /// </summary>
            public AssemblyBuilderAccess? AssemblyAccess;

            /// <summary>Overrides the debug setting, allowing inspecting of node outputs. Defaults to false.</summary>
            public bool Debug;

            public bool Equals(Settings other)
            {
                return AssemblyAccess == other.AssemblyAccess && Debug == other.Debug;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (AssemblyAccess.GetHashCode() * 397) ^ Debug.GetHashCode();
                }
            }
        }

        /// <summary>Compiles a standalone compilation including just this graph and its dependencies.</summary>
        public static GraphCompilation CompileStandalone(LatticeGraph graph, Settings settings = default)
        {
            return CompileFixedSet(GetGraphDependencies(graph), settings);
        }

        /// <summary>Compiles a standalone compilation including all the given graphs and their dependencies.</summary>
        public static GraphCompilation CompileStandalone(IEnumerable<LatticeGraph> graphs, Settings settings = default)
        {
            return CompileFixedSet(graphs.SelectMany(GetGraphDependencies).ToHashSet(), settings);
        }

        internal static HashSet<LatticeGraph> GetGraphDependencies(LatticeGraph startingGraph)
        {
            HashSet<LatticeGraph> graphsInCompilation = new();

            void AddDownstreamRecursive(LatticeGraph graph)
            {
                if (graphsInCompilation.Contains(graph))
                {
                    return;
                }

                graphsInCompilation.Add(graph);

                foreach (LatticeGraph downstream in graph.GetDependencies())
                {
                    AddDownstreamRecursive(downstream);
                }
            }

            AddDownstreamRecursive(startingGraph);

            return graphsInCompilation;
        }

        /// <summary>
        ///     Compiles the given graphs into a single compilation unit, but *not* dependencies. The unit contains the global
        ///     IR graph for all nodes.
        /// </summary>
        internal static GraphCompilation CompileFixedSet(IEnumerable<LatticeGraph> topLevelGraphs,
                                                         Settings settings = default)
        {
            using ProfilerMarker.AutoScope marker = new ProfilerMarker("Lattice Compile").Auto();

#if UNITY_EDITOR
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                Debug.LogWarning(
                    $"There's no point in compiling a Lattice Graph in a import worker process. [{topLevelGraphs.Count()}]");
            }
#endif

            if (EnableVerboseChecks)
            {
                VerifyIRNodeCloneMethods();
            }

            // Compile all graphs in the unit!
            // ===============================
            GraphCompilation compilation = new(settings);

            // All graphs in the compilation must have a unique name. This is because we use names for GUIDs, until we have a better solution.
            HashSet<string> names = new HashSet<string>();
            var graphsWithUniqueNames = new List<LatticeGraph>();
            foreach (var g in topLevelGraphs)
            {
                if (!names.Add(g.name))
                {
                    Debug.LogError($"Lattice graph named [{g.name}] was defined twice. Graphs must have unique names.");
                    continue;
                }
                graphsWithUniqueNames.Add(g);
            }
            topLevelGraphs = graphsWithUniqueNames;

            // inline each graph into the compilation unit. (the children are already inlined at this point)
            // inlining means
            //  - copying the nodes and edges in
            //  - stitch input and output nodes
            //  - create a new semantic info for it?
            foreach (LatticeGraph graph in topLevelGraphs)
            {
#if UNITY_EDITOR
                // Make sure all Graph assets are saved to disk, in the editor. If not, baked data may be out of date.
                // This is because subscene baking runs on the worker processes, and can't read data out of the editor process.
                if (graph.NeedsSaveBeforeRecompile)
                {
                    graph.NeedsSaveBeforeRecompile = false;
                    AssetDatabase.SaveAssetIfDirty(graph);
                    Debug.Log($"(Lattice) Graph saved before recompiling. [{graph}]");
                }
#endif

                compilation.AddToplevelGraph(graph);
            }

            // Bind and replace the late binding IR nodes. This allows nodes in graphs to reference other graphs
            // even if the graph IR bodies for each are built separately.
            BindLateBoundNodes(compilation.Graph);

            // Check for nodes that cannot be analyzed.
            foreach (IRNode node in compilation.Graph.Nodes)
            {
                if (node.ExecutionOnly)
                {
                    throw new Exception($"ICE: Node is not valid for analysis. [{node}]");
                }
            }

            // Verify unique ids
            if (settings.Debug)
            {
                var ids = new Dictionary<string, IRNode>();
                foreach (IRNode node in compilation.Graph.Nodes)
                {
                    if (ids.TryGetValue(node.Id, out IRNode n))
                    {
                        Debug.LogError($"ICE: Two IRNodes have the same Id. Node [{node}] and [{n}]. Id:[{node.Id}]");
                    }
                    ids.Add(node.Id, node);
                }
            }

            // Finish IL generation for the static methods container type. This actually creates the types.
            // Done last because we can't add more static methods to our static class after this.
            // We need to finish construction before we can start analysis because we need to close the generated functions.
            compilation.FinishConstruction();

            // Analysis passes go here!
            // ========================

            TypeCheck(compilation, TypeCheckPhase.LatticeExecutionModel);

            // Emit syntax errors. 
            EmitCompilationErrors(compilation);

            if (!settings.Debug)
            {
                compilation.Graph.WipeDebugHandles();
            }

            if (EnableDeadCodeElimination)
            {
                DeadCodeElimination(compilation);
            }

            compilation.AnalysisFinished = true;

            AssertReverseDependencies(compilation);

            // Optimization passes and lowering go here!
            // ========================
            Pass_ConvertPreviousNodesToPointers(compilation);
            Pass_RemoveIdentityNodes(compilation);

            // The graph is now fully sealed. No more changes!
            compilation.Graph.IsSealed = true;

            compilation.Graph.VerifyGraphStructure(settings);

            TypeCheck(compilation, TypeCheckPhase.LoweredExecutionModel); // Another type check to verify optimizations.

            // Code generation for certain function nodes.
            LiteralStringIRNode.CodeGen(compilation);
            FieldAccessorIRNode.CodeGen(compilation);
            WriteIComponentNode.CodeGen(compilation);

            // Build caches / indexes for quick lookup / analysis:

            // Build our cache of 'state' values. 
            foreach (IRNode n in compilation.Graph.Nodes)
            {
                if (n is PreviousIRNode p)
                {
                    if (p.BackRef == null)
                    {
                        Debug.LogError("ICE: PreviousNode with null BackRef.");
                        continue;
                    }
                    compilation.Graph.ReferencedByPreviousNode.Add(p.BackRef.Node);
                }
            }

            // Node index lookup table.
            int i = 0;
            foreach (IRNode node in compilation.Graph.Nodes)
            {
                compilation.Graph.NodeIndices[node] = i;
                i++;
            }

            return compilation;
        }


        // Emit compilation errors for nodes, but keep going (malformed nodes are valid runtime nodes)
        private static void EmitCompilationErrors(GraphCompilation compilation)
        {
            foreach (var n in compilation.Graph.Nodes)
            {
                if (n.DoNotLogErrors)
                {
                    continue;
                }
                var error = compilation.CompileNode(n).CompilationError;
                if (error != null && error.Node == n)
                {
                    Debug.LogError(
                        $"Syntax Error at [{error.Node}]: {error}\n\n",
                        compilation.Graph.GetOwner(error.Node)?.Last.Graph);
                }
            }
        }

        private static readonly HashSet<Type> validReferenceTypes = new()
        {
            typeof(string),
            typeof(Type),
            typeof(FieldInfo),
            typeof(MethodInfo),
        };

        private static void VerifyIRNodeCloneMethods()
        {
            bool IsValidForMemberwiseClone(Type t)
            {
                if (validReferenceTypes.Contains(t))
                {
                    return true; // Type is an immutable reference type.
                }

                if (!t.IsValueType)
                {
                    // Reference types in general cannot be shallow copied.
                    // This would lead to two refs to the same object. They need to manually new()'d.
                    // However, we allow them if all fields are readonly and valid themselves.
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                  BindingFlags.Instance))
                    {
                        bool valid = f.IsInitOnly &&
                                     IsValidForMemberwiseClone(t) &&
                                     (t.BaseType == null || IsValidForMemberwiseClone(t.BaseType));
                        if (!valid)
                        {
                            return false;
                        }
                    }

                    return true;
                }

                if (t.IsValueType)
                {
                    // Structs must have all fields that are valid types.
                    // Check that the type is valid for MemberwiseCloning.
                    foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                                                      BindingFlags.Instance))
                    {
                        if (!IsValidForMemberwiseClone(field.FieldType))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
#if UNITY_EDITOR
            var nodeTypes = TypeCache.GetTypesDerivedFrom<IRNode>();
            foreach (var n in nodeTypes)
            {
                foreach (var field in n.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (field.DeclaringType != n)
                    {
                        continue; // These fields are already covered by our default implementation, or another check.
                    }

                    var cloneMethod = n.GetMethod(nameof(IRNode.MemberwiseCloneFresh),
                        BindingFlags.Public | BindingFlags.Instance);
                    if (cloneMethod!.DeclaringType == n)
                    {
                        continue; // This IRNode defines a custom clone method. Unsafe, but good to go.
                    }

                    if (!IsValidForMemberwiseClone(field.FieldType))
                    {
                        Debug.LogError(
                            $"IRNode type [{n.Name}] has field [{field.Name}] that cannot be MemberwiseClone()'d. You must implement Clone() manually.");
                    }
                }
            }
#endif
        }

        // Make sure each input is listed in the reverse dependency list. This is somewhat slow.
        private static void AssertReverseDependencies(GraphCompilation compilation)
        {
            if (!EnableVerboseChecks)
            {
                return;
            }

            foreach (IRNode n in compilation.Graph.Nodes)
            {
                foreach (var (id, port) in n.Ports)
                {
                    foreach (var input in port.Inputs)
                    {
                        if (!input.Usages.Contains((id, n)))
                        {
                            Debug.LogError(
                                $"ICE: Node usages is missing input [{id}][{n}]. Graph invariant is broken.");
                        }
                    }
                }
            }
        }

        public static void BindLateBoundNodes(IRGraph graph)
        {
            // For each late bound node, find the reference, replace with a identity node.
            using var _ = CollectionPool<List<LateBindingIRNode>, LateBindingIRNode>.Get(out var lateBindingIRNodes);

            foreach (var n in graph.Nodes)
            {
                if (n is LateBindingIRNode lateBound)
                {
                    lateBindingIRNodes.Add(lateBound);
                }
            }

            foreach (var lateBound in lateBindingIRNodes)
            {
                IRNodeRef targetNode = graph.GetOutputMap(lateBound.BindingPath)[lateBound.OutputPort];

                graph.RedirectNode(lateBound, targetNode.Node);
            }
        }

        private enum TypeCheckPhase
        {
            LatticeExecutionModel,
            LoweredExecutionModel
        }
        
        // Logically, there are two types of IR, the Lattice execution model, and the Lowered execution model. 
        // Right now they follow the same type system rules, but we reserve the right in the future for them to 
        // diverge, to simplify code generation or allow for more complex type systems. For instance, the lowered
        // execution model does not support generics, type inference, or user coercion.

        // Type check the graph. This catches any incorrect graph setup. All errors here are considered ICE,
        // because type errors in user code should be caught earlier and replaced with MalformedIRNodes.
        private static void TypeCheck(GraphCompilation compilation, TypeCheckPhase phase)
        {
            // phase unused for now because the type systems are the same
            
            bool TypesAreCompatible(Type portType, Type inputType)
            {
                // A signature type is an unresolved generic type parameter like "T". Means the inference did not succeed.
                if (portType.IsSignatureType)
                {
                    return false;
                }

                // Standard types. Just check assignment.
                if (portType.IsAssignableFrom(inputType))
                {
                    return true;
                }

                // Exceptions can be plugged into any port, because all values can be exceptions.
                if (typeof(Exception).IsAssignableFrom(inputType))
                {
                    return true;
                }

                // State wrapper types can be plugged into a ref-parameter (aka "managed pointer")
                if (inputType.IsGenericType &&
                    inputType.GetGenericTypeDefinition() == typeof(LatticeState.Wrapper<>) &&
                    inputType.GetGenericArguments()[0].MakeByRefType() == portType)
                {
                    return true;
                }

                // Nullable<T> can be plugged into T, if the port is 'nullable lifted'. 
                // This generates code for the node to automatically skip and return null.
                // Essentially, monadic nullable behavior.
                if (inputType.IsGenericType && inputType.GetGenericTypeDefinition() == typeof(Nullable<>) &&
                    inputType.GetGenericArguments()[0] == portType)
                {
                    return true;
                }

                // List<T> can be plugged into NativeArray<T>. It's a built-in type coercion.
                // This can eventually be replaced once we have non-persistant nodes.
                if (portType.IsGenericType && portType.GetGenericTypeDefinition() == typeof(NativeArray<>) &&
                    inputType.IsGenericType && inputType.GetGenericTypeDefinition() == typeof(List<>) &&
                    portType.GetGenericArguments()[0] == inputType.GetGenericArguments()[0])
                {
                    return true;
                }

                return false;
            }

            foreach (IRNode node in compilation.Graph.Nodes)
            {
                if (compilation.CompileNode(node).OutputType.IsGenericParameter)
                {
                    Debug.LogError(
                        $"ICE: Generic type was found in node [{node}]. All generics must resolve into concrete types after node compilation.");
                    compilation.CannotBeExecuted = true;
                }
            }

            foreach (IRNode node in compilation.Graph.Nodes)
            {
                foreach ((string id, IRPort port) in node.Ports)
                {
                    if (port.Inputs.Count == 0 && !port.IsOptional)
                    {
                        // FunctionIRNodes must be connected on all ports.
                        Debug.LogError($"ICE: Port [{id}] must be connected on node [{node}]. No inputs connected.");
                        compilation.CannotBeExecuted = true;
                    }

                    var compileData = compilation.CompileNode(node);
                    if (compileData.OutputType.IsByRef)
                    {
                        Debug.LogError(
                            $"$ICE: Node has output of type 'ref'. [{node}] ByRef types are not allowed because pointers can't be stored across frames.");
                        compilation.CannotBeExecuted = true;
                    }

                    if (compileData.OutputType.IsByRef)
                    {
                        // Managed types aren't supported yet, as codegen assumes all types can be boxed/nullable-wrapped.
                        Debug.LogError(
                            $"$ICE: Node has a managed output type. [{node}] Managed types are not allowed, yet.");
                        compilation.CannotBeExecuted = true;
                    }

                    foreach (IRNode input in port.Inputs)
                    {
                        Type inputType = compilation.CompileNode(input).OutputType;
                        if (!TypesAreCompatible(port.Type, inputType))
                        {
                            Debug.LogError(
                                $"ICE TypeError: Port [{id}][{port.Type}] cannot be assigned input of type [{inputType}]. Node: [{node}] Input: [{input}]");
                            compilation.CannotBeExecuted = true;
                        }
                    }
                }
            }
        }

        /// <summary>Removes trees of IRNodes that are pure and have no impure users.</summary>
        private static void DeadCodeElimination(GraphCompilation compilation)
        {
            using (CollectionPool<HashSet<IRNode>, IRNode>.Get(out HashSet<IRNode> live))
            using (CollectionPool<HashSet<IRNode>, IRNode>.Get(out HashSet<IRNode> dead))
            {
                foreach (var n in compilation.Graph.Nodes)
                {
                    // Mark all nodes with external references live. (like primary / state nodes) 
                    if (compilation.Graph.HasExternalReferences(n))
                    {
                        MarkLive(n);
                    }

                    // All entity nodes are live, for now, until we rework the LatticeBeginPhase to not need
                    // to loop through them.
                    if (n is EntityIRNode)
                    {
                        MarkLive(n);
                    }
                }

                // Mark all impure nodes live.
                foreach (var n in compilation.Graph.Nodes)
                {
                    if (!n.Pure)
                    {
                        MarkLive(n);
                    }
                }

                void MarkLive(IRNode node)
                {
                    if (live.Add(node))
                    {
                        foreach (var input in node.InputNodes())
                        {
                            MarkLive(input);
                        }

                        if (node is PreviousIRNode p)
                        {
                            MarkLive(p.BackRef.Node);
                        }
                    }
                }

                foreach (var n in compilation.Graph.Nodes)
                {
                    if (!live.Contains(n))
                    {
                        dead.Add(n);
                    }
                }

                // Assert primary and state debug nodes are kept.
                if (EnableVerboseChecks)
                {
                    foreach (var codePath in compilation.Graph.GetCodePaths())
                    {
                        Assert.IsTrue(!dead.Contains(compilation.Graph.GetPrimaryNode(codePath)));

                        var stateDebug = compilation.Graph.GetStateDebugNode(codePath);
                        if (stateDebug != null)
                        {
                            Assert.IsTrue(!dead.Contains(stateDebug));
                        }
                    }
                }

                List<IRNode> topoSort = IRGraph.TopologicalSortNodes(dead);
                topoSort.Reverse();

                // Remove dead nodes.
                foreach (var n in topoSort)
                {
                    compilation.Graph.RemoveNode(n);
                }
            }
        }

        public static void Pass_ConvertPreviousNodesToPointers(GraphCompilation compilation)
        {
            Assert.IsTrue(compilation.AnalysisFinished);

            // Match on: 
            //  - Previous node
            //  - Mutator IR node with p edge going into ref port.
            //  - FieldAccessor for field name 'ref' with same name as input ref port.
            //  - Previous backref matching on that.

            // Becomes:
            //  - StateRef node (outputs pointer) (metadata comes from previous node....?)
            //  - FunctionIRNode accepting pointer input (metadata comes from mutator node???)

            // Assumptions:
            // -- nothing else uses the mutator node besides field accessors
            // -- nothing else uses the ref copy fields besides previous
            // -- only one ref in the mutator
            // -- all refs to the output field are redirected to the function node

            using var _ =
                CollectionPool<List<(PreviousIRNode, MutatorFunctionIRNode, FieldAccessorIRNode, FieldAccessorIRNode)>,
                        (PreviousIRNode, MutatorFunctionIRNode, FieldAccessorIRNode, FieldAccessorIRNode)>
                    .Get(out var matches);

            try
            {
                foreach (var node in compilation.Graph.Nodes)
                {
                    if (node is not PreviousIRNode prevNode)
                    {
                        continue;
                    }

                    // Find a mutator attached to it. If we find one, it must be replaced or fail, because we can't execute it.
                    if (!node.Usages.TryGetFirst(dep => dep.node is MutatorFunctionIRNode, out var mutator))
                    {
                        continue;
                    }

                    // No other consumers of the previous node.
                    Assert.IsTrue(prevNode.Usages.Count <= 1,
                        "ICE: Mutator replacement failed: Previous node has more than 1 usage.");

                    // Must only have a single ref input on the mutator node. (ie. tuple of two)
                    var compileData = compilation.CompileNode(mutator.node);
                    Assert.IsTrue(compileData.OutputType.GetGenericTypeDefinition() == typeof(ValueTuple<,>));

                    // Find the ref port field accessor.
                    bool found = mutator.node.Usages.TryGetFirst(
                        dep => dep.node is FieldAccessorIRNode { FieldName: "Item1" }, out var fieldState);
                    Assert.IsTrue(found, "Mutator node's ref field accessor was removed.");

                    // Find the output value field accessor.
                    // This is ok to be missing, as if the output of the Mutator node is not used, this will get cleaned
                    // up by dead code elimination.
                    var fieldOut = mutator.node.Usages.GetFirst(
                        dep => dep.node is FieldAccessorIRNode { FieldName: "Item2" });

                    // Make sure that the backref points to this node.
                    Assert.IsTrue(prevNode.BackRef.Node == fieldState.node);

                    matches.Add((prevNode, (MutatorFunctionIRNode)mutator.node, (FieldAccessorIRNode)fieldState.node,
                        (FieldAccessorIRNode)fieldOut?.node));
                }

                foreach (var (previous, mutator, fieldState, fieldOut) in matches)
                {
                    Assert.IsTrue(mutator.Usages.Count is 1 or 2);

                    CodePath? ownerNode = compilation.Graph.GetOwner(mutator);
                    Assert.IsTrue(ownerNode.HasValue, "Replacing MutatorIRNode that is a free node not yet supported");

                    var previousData = compilation.CompileNode(previous);
                    var mutatorData = compilation.CompileNode(mutator);
                    var fieldStateData = compilation.CompileNode(fieldState);

                    Assert.AreEqual(previousData.ExecutionPhase, mutatorData.ExecutionPhase);
                    Assert.IsTrue(previousData.Qualifier.Equals(mutatorData.Qualifier));

                    // Add StateRef node, which returns a pointer to state.
                    var stateref = compilation.Graph.AddNode(ownerNode!.Value, "StateRef",
                        new StateRefIRNode(previousData.OutputType));
                    stateref.AddInput(StateRefIRNode.DefaultValuePort,
                        previous.Ports[PreviousIRNode.DefaultValuePort].Inputs[0]);
                    compilation.MetadataDb[stateref] = new Metadata(previousData.Qualifier,
                        typeof(LatticeState.Wrapper<>).MakeGenericType(previousData.OutputType),
                        previousData.ExecutionPhase);

                    // Add standalone function node, which takes the pointer as input.
                    var functionNode = compilation.Graph.AddNode(ownerNode!.Value, mutator.DebugName,
                        new FunctionIRNode(mutator.Method));
                    string statePort = mutator.Ports.FirstOrDefault(pair => pair.Value.Inputs.Contains(previous)).Key;

                    // Add all non-state inputs attached to the original node.
                    foreach (var (id, p) in mutator.Ports)
                    {
                        if (p.Inputs.Contains(previous))
                        {
                            functionNode.AddInput(statePort, stateref);
                            continue;
                        }

                        Assert.IsFalse(p.Type.IsByRef); // Only non-ref ports at this point.

                        foreach (var input in p.Inputs)
                        {
                            functionNode.AddInput(id, input);
                        }
                    }

                    // Transfer nullable lifting onto the new node.
                    functionNode.NullableLiftedPorts = mutator.NullableLiftedPorts;
                    functionNode.MustRunOnMainThread = mutator.MustRunOnMainThread;

                    // This replacement is only valid on mutators that have a single ref input of the state. Ie. It does 
                    // not work on mutators that have several ref inputs.
                    Assert.AreEqual(mutatorData.OutputType.GetGenericTypeDefinition(), typeof(ValueTuple<,>));
                    Type outputType =
                        mutatorData.OutputType.GetGenericArguments()[1]; // second argument is the function return

                    compilation.MetadataDb[functionNode] =
                        new Metadata(mutatorData.Qualifier, outputType, mutatorData.ExecutionPhase);

                    // Remove/Redirect the old nodes.
                    if (fieldOut != null)
                    {
                        compilation.Graph.RedirectNode(fieldOut, functionNode);
                    }
                    
                    // Clear the previous node's BackRef, so we can delete the pointed at state copy node.
                    compilation.Graph.ReleaseNodeRef(previous.BackRef);
                    previous.BackRef = null;

                    // If there are other consumers of the state value after the mutation, add a copy node.
                    if (fieldState.Usages.Count > 0)
                    {
                        Type stateType = mutatorData.OutputType.GetGenericArguments()[0];
                        IRNode stateCopy = compilation.Graph.AddNode(ownerNode!.Value, "StateCopy",
                            CoreIRNodes.CopyState(stateType));
                        stateCopy.AddInput("value", stateref);
                        stateCopy.AddInput(IRNode.BarrierPort,
                            functionNode); // State copy should happen after function node runs.
                        compilation.MetadataDb[stateCopy] =
                            new Metadata(fieldStateData.Qualifier, stateType, fieldStateData.ExecutionPhase);

                        compilation.Graph.RedirectNode(fieldState, stateCopy);
                        // Note: If we ever support mutator chaining, this must be part of the chain.
                    }
                    else
                    {
                        compilation.Graph.RemoveNode(fieldState);
                    }

                    compilation.Graph.RemoveNode(mutator);
                    compilation.Graph.RemoveNode(previous);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("Replacing MutatorIRNodes failed.");
                Debug.LogException(e);
            }
        }

        /// <summary>
        ///     Removes identity nodes and redirects their usages to the original referenced nodes. Has the effect of removing
        ///     redirectors.
        /// </summary>
        public static void Pass_RemoveIdentityNodes(GraphCompilation compilation)
        {
            using var _ = CollectionPool<List<FunctionIRNode>, FunctionIRNode>.Get(out List<FunctionIRNode> matches);
            MethodInfo identityFunc = typeof(CoreIRNodes).GetMethod(nameof(CoreIRNodes.IdentityFunc),
                BindingFlags.Public | BindingFlags.Static);
            Assert.IsNotNull(identityFunc);
            foreach (var n in compilation.Graph.Nodes)
            {
                if (n is FunctionIRNode fNode && fNode.Method != null && fNode.Method.IsGenericMethod &&
                    fNode.Method.GetGenericMethodDefinition() == identityFunc)
                {
                    // Redirect them to their inputs.
                    var input = fNode.Ports["value"].Inputs[0];
                    Assert.IsNotNull(input);

                    compilation.Graph.RedirectNode(fNode, input, true);

                    matches.Add(fNode);
                }
            }

            foreach (var n in matches)
            {
                compilation.Graph.RemoveNode(n);
            }
        }

        private static HashSet<LatticeGraph> GetConnectedComponent(LatticeGraph startingGraph,
                                                                   HashSet<LatticeGraph> allGraphs)
        {
            // Build a map of all the reverse dependencies of LatticeGraphs in the project.
            // Surely there is a better way to do this by now.
            // todo: Find a more performant way to do this.

            Dictionary<LatticeGraph, List<LatticeGraph>> reverseDeps = new(); //paths
            foreach (LatticeGraph graph in allGraphs)
            {
                IEnumerable<LatticeGraph> deps = graph.GetDependencies();
                foreach (LatticeGraph dep in deps)
                {
                    if (!allGraphs.Contains(dep))
                    {
                        throw new Exception("Dependent graph was not provided in compilation!");
                    }

                    if (reverseDeps.TryGetValue(dep, out List<LatticeGraph> referencers))
                    {
                        referencers.Add(graph);
                    }
                    else
                    {
                        reverseDeps.Add(dep, new List<LatticeGraph> { graph });
                    }
                }
            }

            HashSet<LatticeGraph> graphsInCompilation = new();

            void AddUpstreamRecursive(LatticeGraph graph)
            {
                graphsInCompilation.Add(graph);

                foreach (LatticeGraph upstream in reverseDeps[graph])
                {
                    AddUpstreamRecursive(upstream);
                }
            }

            AddUpstreamRecursive(startingGraph);

            void AddDownstreamRecursive(LatticeGraph graph)
            {
                graphsInCompilation.Add(graph);

                foreach (LatticeGraph downstream in graph.GetDependencies())
                {
                    AddDownstreamRecursive(downstream);
                }
            }

            AddDownstreamRecursive(startingGraph);

            return graphsInCompilation;
        }
    }
}
