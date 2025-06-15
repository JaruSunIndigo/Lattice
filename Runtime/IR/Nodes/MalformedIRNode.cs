using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Entities;

namespace Lattice.IR
{
    /// <summary>
    ///     Generated when the authoring node has nothing valid to compile, because there is a syntax error in the
    ///     authored graph setup. A stub which simply returns the syntax error, but allows those errors to still propagate.
    /// </summary>
    public class MalformedIRNode : IRNode
    {
        public MalformedException Reason;

        public MalformedIRNode(string reason)
        {
            Reason = new MalformedException(reason, Environment.StackTrace);
            CheckExceptions = false; // MalformedNode just returns the exception, it doesn't throw it.
            Pure = true;
        } 
        
        public MalformedIRNode(Exception reason)
        {
            Reason = new MalformedException(reason);
            CheckExceptions = false; // MalformedNode just returns the exception, it doesn't throw it.
            Pure = true;
        } 
        
        public override Type CalculateType(List<(string port, Type type)> _) => typeof(MalformedException);

        public override IRNode MemberwiseCloneFresh()
        {
            var n = (MalformedIRNode) base.MemberwiseCloneFresh();
            n.Reason = new MalformedException(n.Reason);
            return n;
        }
    }

    public class MalformedException : Exception
    {
        private readonly string stackTrace;
        public override string StackTrace => stackTrace;

        public MalformedException(string reason, string trace) : base(reason)
        {
            stackTrace = trace;
        }
        
        public MalformedException(Exception inner) : base("Syntax Error: "+inner.Message, inner)
        {
        }
        public MalformedException(string prefix, Exception inner) : base(prefix+inner.Message, inner)
        {
        }
    }
}
