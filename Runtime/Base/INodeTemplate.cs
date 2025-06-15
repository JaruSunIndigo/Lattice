using System;

namespace Lattice.Base
{
    /// <summary>
    ///     A node template defines a node that can be created in the Add Node search box. This allows us to expose
    ///     several different nodes of the same type, but with different default values. For example, all of the ECS Components
    ///     use the ECSComponentNode base type, but expose a template for each individual component field.
    /// </summary>
    public interface INodeTemplate
    {
        /// <summary>Creates a new BaseNode from this template.</summary>
        public BaseNode Build();

        /// <summary>The specific type of the BaseNode this template will create.</summary>
        public Type NodeType { get; }
    }
}
