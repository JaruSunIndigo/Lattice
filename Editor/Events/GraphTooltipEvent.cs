using JetBrains.Annotations;
using Lattice.Editor.Manipulators;
using Lattice.Editor.Views;
using UnityEditor.Experimental.GraphView;
using UnityEngine.UIElements;

namespace Lattice.Editor.Events
{
    /// <summary>
    ///     Sends an event that finds tooltips when not using <see cref="IHasGraphTooltip" /> types. This event bubbles up.<br />
    ///     See <see cref="GraphTooltipManipulator"/> if you want to add a tooltip to an element.
    /// </summary>
    internal sealed class GraphTooltipEvent : EventBase<GraphTooltipEvent>
    {
        /// <summary>The tooltip shown.</summary>
        public string Tooltip { get; private set; }

        /// <summary>Position the tooltip is shown relative to <see cref="Source"/>.</summary>
        public GraphTooltipPosition Position { get; private set; } = GraphTooltipPosition.Top;

        /// <summary>Optional tooltip to be displayed. If provided, <see cref="Tooltip"/> is unused.</summary>
        [CanBeNull]
        public GraphElement TooltipOverride { get; private set; }

        /// <summary>The element that set the tooltip. Null if no tooltip was found in the propagation path.</summary>
        [CanBeNull]
        public VisualElement Source { get; private set; }
        
        /// <summary>Sets the tooltip and stops the event propagating.</summary>
        public void SetTooltip(VisualElement source, [CanBeNull] string value, GraphTooltipPosition position = GraphTooltipPosition.Top)
        {
            Source = source;
            Tooltip = value ?? "";
            Position = position;
            StopPropagation();
        }
        
        /// <summary>Sets the tooltip and stops the event propagating.</summary>
        public void SetTooltip(VisualElement source, GraphElement value, GraphTooltipPosition position = GraphTooltipPosition.Top)
        {
            Source = source;
            TooltipOverride = value;
            Position = position;
            StopPropagation();
        }
        
        /// <inheritdoc />
        protected override void Init()
        {
            base.Init();
            bubbles = true;
            Tooltip = "";
            TooltipOverride = null;
            Position = GraphTooltipPosition.Top;
            Source = null;
        }
    }
}
