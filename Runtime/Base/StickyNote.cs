using System;
using Unity.Mathematics;
using UnityEngine;

namespace Lattice.Base
{
    /// <summary>Serializable Sticky note class</summary>
    [Serializable]
    public class StickyNote
    {
        public float2 position;
        public string title;
        public string content = "Description";

        public StickyNote(string title, Vector2 position)
        {
            this.title = title;
            this.position = position;
        }
    }
}
