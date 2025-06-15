using System;
using System.Collections.Generic;
using Lattice.Base;

namespace Lattice.IR
{
    /// <summary>
    ///     A node that references a node in the past frame. This allows cycles in the broader graph, and is used
    ///     liberally to represent 'state' between frames. It takes a Default input for what value should return on the first
    ///     frame (of either the game or the entity).
    /// </summary>
    public class PreviousIRNode : IRNode
    {
        // This is not a real port. It's only considered when compiling.
        public const string BackRefFakePort = "BackRef";

        public const string DefaultValuePort = "default";

        // ports: [BackRef, default]

        // We keep the cyclical ref outside of the normal inputs, so that it doesn't affect other graph operations.
        // For all intents and purposes, this node is a constant node that doesn't do anything with time, except
        // during compilation and inner execution.
        public IRNodeRef BackRef;

        public PreviousIRNode()
        {
            CheckExceptions = false;

            AddPort(Type.MakeGenericMethodParameter(0), DefaultValuePort);
        }

        public PreviousIRNode(IRNodeRef node) : this()
        {
            BackRef = node;
        }

        public override Type CalculateType(List<(string port, Type type)> inputTypes)
        {
            // Pass through input type from the first port. Assert it's the same as the one passed on the default.
            Type backRefType = GetInputType(inputTypes, BackRefFakePort);
            Type defaultRefType = GetInputType(inputTypes, DefaultValuePort);

            // If a type is missing, it's likely because we're in a cycle, so we don't know the type.
            if (defaultRefType == null || backRefType == null)
            {
                return typeof(ITypeUnknown);
            }

            Ports[DefaultValuePort].Type = backRefType;

            return backRefType;
        }

        public override IRNode MemberwiseCloneFresh()
        {
            // The BackRef will still need to be patched after cloning.
            var n = (PreviousIRNode) base.MemberwiseCloneFresh();
            n.BackRef = null;
            return n;
        }
    }
}
