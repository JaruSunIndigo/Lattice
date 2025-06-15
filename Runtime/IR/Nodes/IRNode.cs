using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using UnityEngine.Pool;

namespace Lattice.IR
{
    // IR representation.
    //   - Node-based, fully DAG. Simpler than the authored representation.
    //   - No ref edges. 
    //   - One authored node -> many IRNodes.
    //   - Every node has an authored owner. (could be node or an edge? hm.)
    //   - Fully typed.

    // Nodes have ports. One port can have multiple edges attached.
    // Every node has one output value, 'output ports' are implemented as accessor nodes.
    // Automatic coercion is inlined: just an extra node type.

    // All nodes are in a massive singular graph. 
    //   - No reference nodes!
    //   - Subgraphs have been inlined.

    /// <summary>A port on an IRNode. Holds connections for the node's inputs.</summary>
    public class IRPort
    {
        /// <summary>
        ///     A list of input nodes connected to the port. Usually, only one, unless the IRNode accepts several inputs (like
        ///     Collect).
        /// </summary>
        public List<IRNode> Inputs;

        /// <summary>
        ///     The type of the input value. May be a generic parameter early on in the compilation, before type inference.
        ///     (aka Signature Type or 0!!).
        /// </summary>
        public Type Type;

        // todo: We should remove optional ports in favor of simply instantiating default parameters as constant node inputs.
        // Optional ports are used to support FunctionIRNodes with default parameters. 
        /// <summary>Whether the port can support having no inputs connected.</summary>
        public bool IsOptional;

        public IRPort CloneNew()
        {
            return new IRPort {
                Inputs = new List<IRNode>(),
                Type = Type,
                IsOptional = IsOptional
            };
        }
    }

    /// <summary>
    ///     The fundamental primitive of a Lattice graph. A directed graph of IRNodes is built and then compiled to .NET
    ///     IL, when executing Lattice. Think of each type of IRNode as an 'instruction' in assembly. Ie. "Load", "Set",
    ///     "Call". There are very few inheritors of IRNode, because each one must be implemented manually in the compiler. In
    ///     Lattice, 90% of compiled operations are <see cref="FunctionIRNode" />, the remaining operators are similar to
    ///     control flow.
    /// </summary>
    public abstract class IRNode
    {
        public const string BarrierPort = "$barrier"; // Must not be a valid C# param name.

        // This is a list to support multiple inputs per port.
        public Dictionary<string, IRPort> Ports = new(); // port, input node.int 

        // The list of ports that pull the value of this node. Essentially, reverse dependency lookup. This list
        // must stay in sync with IRPort.Inputs as edges are added/removed.
        public List<(string portId, IRNode node)> Usages = new();

        /// <summary>
        ///     A unique identifier that is stable between compilations. Every time the authored graph is compiled, this id
        ///     stays the same, so that executed values still line up.
        /// </summary>
        /// <remarks>Currently: (foreach path: {LatticeNode.GUID}*)_{Number_Within_LatticeNode}</remarks>
        public string Id;

        /// <summary>This node can only be calculated after this system type runs in the PlayerLoop.</summary>
        /// <remarks>
        ///     A manual specification of the phase for this node. Most nodes won't have one and the effective the phase will
        ///     be calculated during compilation.
        /// </remarks>
        [CanBeNull]
        public Type SystemPhase;

        /// <summary>
        ///     Marks that this node may throw an exception. Set this to true, and Lattice will wrap its execution in a
        ///     exception handler. If false, errors thrown by this node will bubble up, and stop the entire lattice execution for
        ///     the phase. Defaults to true for safety, but this incurs a large performance cost, so use it thoughtfully.
        /// </summary>
        public bool CheckExceptions = true;

        /// <summary>
        ///     Whether the execution function is mathematically pure (ie. no side effects). Pure IR nodes can be safely
        ///     removed from the compilation if nothing uses them.
        /// </summary>
        public bool Pure = false;

        /// <summary>
        ///     Marks that a node cannot generate code, and only exists as an operation in the Lattice abstract machine. These
        ///     operations must be lowered (replaced) with executable operations before code generation. These nodes are useful for
        ///     representing high-level operations on data that could be executed, but would be too costly or incur allocation if
        ///     actually emitted.
        /// </summary>
        public bool CannotExecute = false;

        /// <summary>
        ///     Marks that a node cannot participate in Lattice high-level analysis. This is because it doesn't follow the
        ///     semantics of the Lattice Abstract Machine. For example, using pointers. These nodes can only be created by
        ///     optimizations passes that run after analysis and just before code execution.
        /// </summary>
        public bool ExecutionOnly = false;

        /// <summary>
        ///     True, if this node must be executed from the main thread. This can be true if the node uses EntityManager, or
        ///     other Unity main-thread APIs. This will cause a synchronization point in the job graph.
        /// </summary>
        public bool MustRunOnMainThread = false;

        /// <summary>Used for toString(). Gets set when adding the node to the compilation.</summary>
        public string DebugPath { get; internal set; } = "(Node not added to graph!)";

        /// <summary>Set this to change the name used in DebugPath.</summary>
        public string DebugName;

        /// <summary>
        ///     If set to true, the compilation and execution of graphs will not log errors to the Unity console for syntax
        ///     and runtime errors. Everything else will act like normal, just no logging. ICE's will still log. This is useful for
        ///     unit tests which would fail on console messages, but where we still want to test to make sure errors propagate
        ///     correctly.
        /// </summary>
        internal bool DoNotLogErrors = false;

        protected IRNode()
        {
            // Every node has a "Barrier" port, which allows it to wait for another node to execute before completing.
            // This is similar to the explicit barrier node we used to have, however, this is more flexible.
            AddPort(typeof(object), BarrierPort, optional: true);
        }

        /// <summary>Calculates this nodes propagated type from its inputs.</summary>
        public abstract Type CalculateType(List<(string port, Type type)> valueTuples);

        // Clones this node and returns a node with all the same settings.
        // Wipes state that gets set when we add the node. So this is a 'fresh' node.
        public virtual IRNode MemberwiseCloneFresh() {
            var node = (IRNode) MemberwiseClone();
            
            // reject if there are other fields that are not value types? hm.
            
            node.Id = null;
            node.DebugPath = null;
            node.DebugName = null;
            node.Usages = new();

            // Copy ports.
            // We don't copy the inputs, because those are edges in the graph.
            // We also don't copy usages, because those are edges in the graph.
            node.Ports = new();
            foreach (var p in Ports) {
                node.Ports.Add(p.Key, p.Value.CloneNew());
            }
            
            return node;
        }

        /// <summary>
        ///     Get the IRNode connected to input port {portId}. Assumes only a single connection. If your node has several
        ///     inputs on this port, use Ports directly.
        /// </summary>
        public IRNode GetInput(string portId, bool allowMissing = false)
        {
            if (!Ports.TryGetValue(portId, out IRPort p))
            {
                throw new Exception($"No input port with id [{portId}] for node [{this}].");
            }

            if (p.Inputs.Count > 1)
            {
                throw new Exception(
                    $"Expected only a single input on port [{portId}] for node [{this}]. Found [{p.Inputs.Count}]");
            }

            if (p.Inputs.Count == 0)
            {
                if (allowMissing)
                {
                    return null;
                }

                throw new Exception($"No inputs connected on port [{portId}] for node [{this}].");
            }

            return p.Inputs[0];
        }

        protected void AddPort(Type type, string id, IRNode node = null, bool optional = false)
        {
            if (Ports.ContainsKey(id))
            {
                throw new Exception($"Node [{this}] already contains port id [{id}].");
            }

            Ports[id] = new IRPort
            {
                Inputs = new(),
                Type = type,
                IsOptional = optional
            };

            if (node != null)
            {
                AddInput(id, node);
            }
        }

        public void AddInput(string portId, IRNode node)
        {
            if (!Ports.TryGetValue(portId, out IRPort port))
            {
                throw new Exception($"ICE: No port [{portId}] on node [{this}]. Cannot add input.");
            }

            port.Inputs.Add(node);
            node.Usages.Add((portId, this));
        }

        public void RemoveInput(string portId, IRNode node)
        {
            if (!Ports.TryGetValue(portId, out IRPort port))
            {
                throw new Exception($"ICE: No port [{portId}] on node [{this}]. Cannot remove input.");
            }

            if (!port.Inputs.Remove(node))
            {
                throw new Exception(
                    $"ICE: Node [{node}] was not connected to input port [{portId}]. Cannot remove input.");
            }

            node.Usages.Remove((portId, this));
        }

        public void RemoveAllInputs()
        {
            foreach (var (portId, port) in Ports)
            {
                while (port.Inputs.Count > 0)
                {
                    RemoveInput(portId, port.Inputs[^1]);
                }
            }
        }

        public bool HasUsage(IRNode node)
        {
            foreach ((string _, IRNode usage) in Usages)
            {
                if (usage == node)
                {
                    return true;
                }
            }

            return false;
        }

        public IEnumerable<IRNode> InputNodes()
        {
            return Ports.Values.SelectMany(i => i.Inputs);
        }

        protected Type GetInputType(IEnumerable<(string, Type)> inputTypes, string port)
        {
            foreach ((string, Type) input in inputTypes)
            {
                if (input.Item1 == port)
                {
                    return input.Item2;
                }
            }

            return null;
        }

        public int NumInputs()
        {
            int count = 0;
            foreach (var p in Ports)
            {
                count += p.Value.Inputs.Count;
            }
            return count;
        }

        public override string ToString()
        {
            return $"{DebugPath}:{DebugName}";
        }
    }

    /// <summary>
    ///     A qualifier represents a 'type' of entity. Ie. 'all entities that use graph A' This allows us to tag nodes in
    ///     the graph by qualifier, so we can determine which entities are provided for an entity node.
    /// </summary>
    public struct Qualifier
    {
        // A unique string for each of the lattice graph asset.
        // We could change this to a hash128 to optimize.
        public readonly string guid;
        private readonly string debugName;

        public Qualifier(string guid, string debugName = "no_debug_name")
        {
            this.guid = guid;
            this.debugName = debugName;
        }

        public bool Equals(Qualifier other)
        {
            return guid == other.guid;
        }

        public override bool Equals(object obj)
        {
            return obj is Qualifier other && Equals(other);
        }

        public override int GetHashCode()
        {
            return guid != null ? guid.GetHashCode() : 0;
        }

        public override string ToString()
        {
            return guid;
        }

        public string ShortName()
        {
            return debugName;
        }
    }

    /// <summary>
    ///     An equality comparer we can use with .NET dictionaries, hashsets. This avoids using the default
    ///     ObjectEqualityComparer which boxes structs.
    /// </summary>
    public class QualifierComparer : IEqualityComparer<Qualifier>
    {
        public bool Equals(Qualifier x, Qualifier y)
        {
            return x.guid == y.guid;
        }

        public int GetHashCode(Qualifier obj)
        {
            return obj.guid != null ? obj.guid.GetHashCode() : 0;
        }
    }

    /// <summary>A basic unit value. Used to represent the return type of void functions.</summary>
    public struct Unit
    {
        /// <inheritdoc />
        public override string ToString()
        {
            return "Unit()";
        }
    }
}
