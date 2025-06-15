using System;
using System.Collections.Generic;
using Lattice.IR;
using Unity.Entities;
using UnityEngine;

namespace Lattice
{
    /// <summary>This component stores any per-entity state from the executing Lattice graph.</summary>
    public class LatticeState : IComponentData
    {
        // Todo(perf): State is currently stored as a managed list. This is a bit slow, and we can do better by
        // generating a struct type to hold the exact state every entity needs. This may still need to be stored as 
        // an object reference, because we can't inject new IComponentData into ECS at runtime.

        /// <summary>
        ///     The set of Lattice values this entity stores for use in the next frame. This is automatically determined by
        ///     analyzing the graph. The string key is the <see cref="IRNode.Id" /> of the value.
        /// </summary>
        public List<(string nodeId, IWrapper state)> State = new();

        /// <summary>Set the given value into the state for the given IR node identifier.</summary>
        public void SetValue<T>(string nodeId, T value)
        {
            lock (State)
            {
                var wrapper = new Wrapper<T>(value);
                for (int i = 0; i < State.Count; i++)
                {
                    var (node, state) = State[i];
                    if (node == nodeId)
                    {
                        State[i] = (nodeId, wrapper);
                        return;
                    }
                }

                State.Add((nodeId, wrapper));

                // A sanity check, to tell us when we need to implement a more efficient scheme for lattice state.
                if (State.Count > 64)
                {
                    Debug.LogWarning(
                        "(Lattice) Lattice entity has more than 64 state fields. This may affect performance.");
                }
            }
        }

        /// <summary>Gets the state value for the given node.</summary>
        public bool TryGetValue(string nodeId, out object obj)
        {
            lock (State)
            {
                foreach ((string node, IWrapper state) in State)
                {
                    if (node == nodeId)
                    {
                        obj = state.Get();
                        return true;
                    }
                }

                obj = default;
                return false;
            }
        }

        /// <summary>Gets the wrapper pointer around the state for the given node.</summary>
        public bool TryGetWrapper<T>(string nodeId, out Wrapper<T> wrapper)
        {
            lock (State)
            {
                foreach ((string node, IWrapper state) in State)
                {
                    if (node == nodeId)
                    {
                        wrapper = (Wrapper<T>)state;
                        return true;
                    }
                }

                wrapper = null;
                return false;
            }
        }

        /// <summary>
        ///     A typed wrapper struct around state values. We use this so that Entity values are properly remapped when this
        ///     component is copied. ManagedObjectRemap finds entity values based on property type, so a generic wrapper will
        ///     create a Entity field to remap.
        /// </summary>
        /// <remarks>We also use this to pass around a pointer to the state during execution.</remarks>
        [Serializable]
        public class Wrapper<T> : IWrapper
        {
            public T Obj;

            // Default constructor needed during managed object copy when copying state.
            public Wrapper()
            {
                Obj = default;
            }

            public Wrapper(T val)
            {
                Obj = val;
            }

            public object Get() => Obj;
        }

        public interface IWrapper
        {
            public object Get();
        }
    }
}
