using System;
using System.Collections.Generic;
using System.Reflection;
using Lattice.Base;
using Lattice.IR;
using Unity.Entities;
using UnityEngine;

namespace Lattice {


    /// <summary>
    /// Marker interface to note that a system represents a Lattice phase.
    /// </summary>
    public interface ILatticePhaseSystem
    {
    }

    /// <summary>
    ///  Static methods.
    /// </summary>
    public static class LatticePhases
    {
        private static Type defaultPhase;

        /// <summary>Returns the System tagged with <see cref="LatticeDefaultPhaseAttribute" />.</summary>
        public static Type GetLatticeDefaultPhase()
        {
            if (defaultPhase != null)
            {
                return defaultPhase;
            }

            Type t = null;
            foreach (var s in TypeManager.GetSystems())
            {
                if (s.GetCustomAttribute<LatticeDefaultPhaseAttribute>() != null)
                {
                    if (t != null)
                    {
                        Debug.LogError(
                            $"Found more than one System with attribute [LatticeDefaultPhase]. Did you forget to define LATTICE_CUSTOM_PHASES? [{s}][{t}]");
                    }
                    t = s;
                }
            }
            defaultPhase = t;
            if (defaultPhase == null)
            {
                throw new Exception(
                    "Couldn't find default phase for Lattice. No system had [LatticeDefaultPhase] attribute.");
            }
            return t;
        }
    }
}
