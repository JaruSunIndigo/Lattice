using System;
using Lattice.Utils;
using Unity.Assertions;

namespace Lattice.Base
{
    /// <summary>
    ///     A sigil returned by a node's type when the type cannot be inferred. This is propagated through the graph when
    ///     resolving compilation with cycles (ie. previous nodes). You could also think of this value as the 'Bottom' type.
    ///     Ie, the equivalent of RequiredQualifiers.None.
    /// </summary>
    public interface ITypeUnknown { }
}
