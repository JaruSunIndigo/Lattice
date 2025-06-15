using Lattice.Editor.Events;
using Lattice.Editor.Views;
using Unity.Assertions;
using UnityEngine.UIElements;

namespace Lattice.Editor.Manipulators
{
    /// <summary>Add to a <see cref="VisualElement"/> to show simple tooltips in a <see cref="LatticeGraphWindow"/>.</summary>
    public sealed class GraphTooltipManipulator : Manipulator
    {
        /// <summary>The tooltip to decorate the target element with when hovered.</summary>
        public string Tooltip { get; set; }
        
        /// <summary>Position the tooltip is shown relative to <see cref="Manipulator.target"/>.</summary>
        public GraphTooltipPosition Position { get; private set; } = GraphTooltipPosition.Top;

        /// <inheritdoc />
        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<GraphTooltipEvent>(OnGraphTooltipEvent);
        }

        /// <inheritdoc />
        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<GraphTooltipEvent>(OnGraphTooltipEvent);
        }
        
        private void OnGraphTooltipEvent(GraphTooltipEvent evt)
        {
            evt.SetTooltip(target, Tooltip);
        }
    }
    
    /// <summary>
    /// Shows a <see cref="Lattice.Editor.Views.FakeNodeView"/> when a node is hovered.<br/>
    /// Used when <see cref="Lattice.Base.BaseNode.CollapsedToState"/> is active.
    /// </summary>
    public sealed class CollapsedToGraphTooltipManipulator : Manipulator
    {
        private FakeNodeView fakeNodeView;
        
        /// <inheritdoc />
        protected override void RegisterCallbacksOnTarget()
        {
            Assert.IsTrue(target is BaseNodeView);
            target.RegisterCallback<GraphTooltipEvent>(OnGraphTooltipEvent);
            target.RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        /// <inheritdoc />
        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<GraphTooltipEvent>(OnGraphTooltipEvent);
            target.UnregisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        private void OnDetachFromPanel(DetachFromPanelEvent evt) => fakeNodeView?.RemoveFromHierarchy();

        private void OnGraphTooltipEvent(GraphTooltipEvent evt)
        {
            BaseNodeView nodeView = (BaseNodeView)target;
            if (fakeNodeView == null)
            {
                fakeNodeView = new FakeNodeView(nodeView);
                fakeNodeView.AddToClassList(FakeNodeView.UssClassName + "--collapsedToTooltip");
            }
            else
            {
                fakeNodeView.Update();
            }
            evt.SetTooltip(target, fakeNodeView, GraphTooltipPosition.Right);
        }
    }
}
