using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;

namespace Lattice.IR
{
    public struct WorkUnitJob : IJob
    {
        // store a function pointer
        // store the context..?
        public GCHandle Delegate;
        public GCHandle Context;
        
        public void Execute()
        {
            ILGeneration.ExecuteGraph d = Delegate.Target as ILGeneration.ExecuteGraph;
            if (d == null) {
                Debug.LogError("Null Delegate");
                return;
            }
            
            d(Context.Target as IRExecution, (Context.Target as IRExecution).EntityManager);
        }
    }
}
