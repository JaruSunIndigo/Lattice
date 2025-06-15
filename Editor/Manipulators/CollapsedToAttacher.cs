#nullable enable

using System;
using System.Reflection;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lattice.Editor.Manipulators
{
    /// <summary>A wrapper for <see cref="Attacher"/> that robustly handles cases where the element and target are detached.</summary>
    internal sealed class CollapsedToAttacher
    {
        private readonly Attacher attacher;
        private readonly Action targetWasDetachedEvent;

        public CollapsedToAttacher(
            VisualElement anchored,
            VisualElement target,
            SpriteAlignment alignment,
            float distance,
            Action targetWasDetachedEvent
        )
        {
            this.targetWasDetachedEvent = targetWasDetachedEvent;
            attacher = new Attacher(anchored, target, alignment) { distance = distance };
            RegisterCallbacks();
        }

        private void RegisterCallbacks()
        {
            attacher.element.RegisterCallback<DetachFromPanelEvent>(DetachedThis);
            attacher.target.RegisterCallback<DetachFromPanelEvent>(DetachedTarget);
        }

        private void DetachedThis(DetachFromPanelEvent evt) => Detach();

        private void DetachedTarget(DetachFromPanelEvent evt)
        {
            Detach();
            targetWasDetachedEvent.Invoke();
        }

        private void UnregisterCallbacks()
        {
            attacher.element.UnregisterCallback<DetachFromPanelEvent>(DetachedThis);
            attacher.target.UnregisterCallback<DetachFromPanelEvent>(DetachedTarget);
        }

        public void Detach()
        {
            VisualElement anchored = attacher.element;
            attacher.Detach();
            
            // Reset inline styles added by Attacher.
            // This is required due to a bug in Attacher and `.layout` that doesn't clear this value.
            // https://issuetracker.unity3d.com/issues/visualelement-becomes-stuck-when-using-unityeditor-dot-experimental-dot-graphview-dot-attacher
            typeof(VisualElement)
                .GetMethod("ClearManualLayout", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(anchored, null);
            
            UnregisterCallbacks();
        }
    }
}
