using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Assertions;

namespace Lattice.IR
{
    /// <summary>
    ///     Stores the most recent executions for Lattice graphs. This lets you view past runs and see the final values of
    ///     nodes, for past game sessions. These will be available in the Lattice UI.
    /// </summary>
    public class ExecutionHistory
    {
        /// <summary>The most recent N executions of the LatticeBeginSystem.</summary>
        public static List<(double time, IRExecution execution)> History = new();

        [CanBeNull]
        public static IRExecution MostRecent => History.Count == 0 ? null : History[0].execution;

        /// <summary>Add an execution to the history.</summary>
        public static void Add(IRExecution execution)
        {
            Assert.IsNotNull(execution);
            History.Insert(0, (Time.realtimeSinceStartup, execution));

            while (History.Count > 5)
            {
                History.RemoveAt(4);
            }
        }
    }
}
