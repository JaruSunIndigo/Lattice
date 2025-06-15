using System;

namespace Lattice.Base
{
    /// <summary>Lattice exception that indicates something went wrong with a port.</summary>
    public class LatticePortException : Exception
    {
        public string PortIdentifier { get; }

        public LatticePortException(string message, string portIdentifier) : base(message)
        {
            PortIdentifier = portIdentifier;
        }
    }

    public sealed class LatticePortRequirementException : LatticePortException
    {
        public LatticePortRequirementException(string message, string portIdentifier) : base(message, portIdentifier) { }
    }
}
