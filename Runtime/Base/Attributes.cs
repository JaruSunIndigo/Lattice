using System;
using Unity.Assertions;
using UnityEngine;

namespace Lattice.Base
{
    /// <summary>
    ///     Marking this on an input parameter to a C# function will render the parameter as a property on the node in the
    ///     Lattice graph editor. The value chosen in the inspector will get serialized to the graph.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class PropAttribute : Attribute { }

    /// <summary>Register the node in the NodeProvider class. The node will also be available in the node creation window.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class NodeCreateMenuAttribute : Attribute
    {
        public string MenuPath;

        /// <summary>Register the node in the NodeProvider class. The node will also be available in the node creation window.</summary>
        /// <param name="menuPath">Path in the menu, use / as folder separators</param>
        public NodeCreateMenuAttribute(string menuPath = null)
        {
            MenuPath = menuPath;
        }
    }

    /// <summary>Set a custom drawer for a field. It can then be created using the FieldFactory</summary>
    [AttributeUsage(AttributeTargets.Class)]
    [Obsolete("You can use the standard Unity CustomPropertyDrawer instead.")]
    public class FieldDrawerAttribute : Attribute
    {
        public Type fieldType;

        /// <summary>Register a custom view for a type in the FieldFactory class</summary>
        /// <param name="fieldType"></param>
        public FieldDrawerAttribute(Type fieldType)
        {
            this.fieldType = fieldType;
        }
    }

    /// <summary>Allow you to have a custom view for your stack nodes</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class CustomStackNodeView : Attribute
    {
        public Type stackNodeType;

        /// <summary>Allow you to have a custom view for your stack nodes</summary>
        /// <param name="stackNodeType">The type of the stack node you target</param>
        public CustomStackNodeView(Type stackNodeType)
        {
            this.stackNodeType = stackNodeType;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class HideLabel : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public class SettingAttribute : Attribute
    {
        public string name;

        public SettingAttribute(string name = null)
        {
            this.name = name;
        }
    }

    /// <summary>
    ///     Add this to a static method to inject new INodeTemplate's into the "Add Node" search box.
    ///     <code>public static IEnumerable<INodeTemplate> MyNodes() {}</code>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class AddToNodeMenuAttribute : Attribute { }

    /// <summary>
    ///     Add this to the field of a IComponentData struct, and it will only allow itself to be read from Lattice but
    ///     not set. The field will show up as an output port, but not as an input port.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class LatticeReadOnlyAttribute : Attribute { }

    /// <summary>
    ///     Used to control which LatticePhase this node will run within. Currently this guarantees nodes will run after
    ///     this phase, but not necessarily within it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class LatticePhaseAttribute : Attribute
    {
        public Type Phase;
        
        public LatticePhaseAttribute(Type phase)
        {
            if (!typeof(ILatticePhaseSystem).IsAssignableFrom(phase))
            {
                Debug.LogError($"Type does not extend LatticePhaseSystem. [{phase}]");
            }

            Phase = phase;
        }
    }

    /// <summary>
    ///     Lattice will place nodes in this system by default, if they do not specify or inherit a phase. Only one
    ///     default phase system can exist in the project.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class LatticeDefaultPhaseAttribute : Attribute { }

    /// <summary>
    ///     Tells Lattice that this field is never Entity.Null. Useful to avoid handling the null cases if you know it's
    ///     always present.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class EntityNotNullAttribute : Attribute { }
}
