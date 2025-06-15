using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Lattice.Base;
using Lattice.IR;
using UnityEngine;

namespace Lattice.Nodes
{
    // In the compiler, a LatticeNode is roughly a syntax node in the Abstract Syntax Tree. These are represented with 
    // views to the user, and are not specific to a certain code path. Rather they are the base data format we edit the 
    // code on top of.
    
    /// <summary>Base class for nodes that represent calculated values. Nearly all nodes in our value graph are LatticeNodes.</summary>
    [Serializable]
    public abstract class LatticeNode : BaseNode
    {
        public new LatticeGraph Graph => (LatticeGraph) base.Graph;

        /// <summary>
        /// The path of this node relative to its asset graph.
        /// </summary>
        protected CodePath Path => ToRootPath();

        /// <summary>
        ///     If the node could not successfully be created. The underlying C# method may not exist, or other 'syntax-like'
        ///     errors. This exception represents why this node couldn't be created.
        /// </summary>
        [NonSerialized]
        public Exception MalformedReason;

        // todo: This should be a flag, and the type should be inferred from the compilation.
        // This allows us to determine whether this is a stateful node or not.
        /// <summary>The type of the state value for this node, or null if this node is fully pure. Most nodes should be pure.</summary>
        public virtual bool IsStatefulNode => false;

        /// <summary>
        ///     The set of Read/Write refs this node requires / writes to. Having any turns this node into an 'action'.
        ///     Identifier strings.
        /// </summary>
        [NonSerialized]
        public List<string> ActionPorts = new();

        /// <summary>
        ///     If set to true, the compilation and execution of graphs will not log errors to the Unity console for syntax and runtime errors.
        ///     Everything else will act like normal, just no logging. ICE's will still log. This is useful for unit tests which would fail on console
        ///     messages, but where we still want to test to make sure errors propagate correctly.
        /// </summary>
        [SerializeField]
        [HideInInspector]
        internal bool DoNotLogErrors = false;

        /// <summary>Allows a node to specify other nodes that will be inputs when this node is compiled.</summary>
        public virtual IEnumerable<LatticeNode> AdditionalCompileInputs()
        {
            return Enumerable.Empty<LatticeNode>();
        }

        /// <summary>
        ///     Specifies how this node compiles down to IRNodes. This is the work-horse of LatticeNode. Creates IRNodes and add
        ///     them to the compilation, setting up any necessary properties or connections between them.
        /// </summary>
        public abstract void CompileToIR(IRGraph compilation);

        // Useful for reference nodes which add extra edges to the compilation.
        public virtual void AddAdditionalEdges(GraphCompilation compilation) { }

        /// <summary>
        ///     Whether this node, locally, depends on time. If it doesn't depend on time, it only needs to be calculated once
        ///     and can be cached.
        /// </summary>
        /// <remarks>
        ///     This property is transitive, so any node that itself depends on a time-dependent node will become time
        ///     dependent.
        /// </remarks>
        /// <remarks>We treat nodes as fully static by default, so this is false.</remarks>
        public virtual bool DependsOnTime => false;

        public override bool IsRenamable => true;

        // The set of ports on the node that act like inputs in terms of data flow (normal inputs, including ref edges)
        public IEnumerable<NodePort> GetLogicalInputs()
        {
            return InputPorts.Where(p => !p.portData.isRefType).Concat(
                OutputPorts.Where(p => p.portData.isRefType));
        }

        // The set of ports on the node that act like outputs in terms of data flow (normal outputs, no ref edges)
        public IEnumerable<NodePort> GetValueOutputs()
        {
            return OutputPorts.Where(p => !p.portData.isRefType);
        }

        public static bool TryGetValueForPort<T>(List<(string portIdentifier, object value)> inputs,
                                                 string portIdentifier,
                                                 out T value)
        {
            foreach (var input in inputs)
            {
                if (input.portIdentifier == portIdentifier)
                {
                    value = (T)input.value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public static T GetValueForPort<T>(List<(string portIdentifier, object value)> inputs,
                                           string portIdentifier)

        {
            foreach (var input in inputs)
            {
                if (input.portIdentifier == portIdentifier)
                {
                    return (T)input.value;
                }
            }

            throw new Exception(
                $"No value was provided on port [{portIdentifier}]. Only [{string.Join(",", inputs.Select(i => i.portIdentifier))}]");
        }

        /// <summary>
        ///     Returns the set of Lattice Graphs this node references. We use this to compute the set of reference lattice
        ///     graphs in a compilation unit.
        /// </summary>
        public virtual IEnumerable<LatticeGraph> GetDependencies()
        {
            yield break;
        }

        public override string ToString()
        {
            return GetPath();
        }

        // public CodePath ToRelativePath()
        // {
        //     return new CodePath(this);
        // }
        
        public CodePath ToRootPath()
        {
            return new CodePath(Graph, this);
        }
    }

}
