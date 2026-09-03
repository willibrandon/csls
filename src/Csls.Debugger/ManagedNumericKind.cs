namespace Csls.Debugger;

/// <summary>
/// Identifies one normalized built-in numeric domain for safe host-side operations.
/// </summary>
internal enum ManagedNumericKind
{
    /// <summary>
    /// Uses signed 32-bit integral arithmetic.
    /// </summary>
    Int32,

    /// <summary>
    /// Uses unsigned 32-bit integral arithmetic.
    /// </summary>
    UInt32,

    /// <summary>
    /// Uses signed 64-bit integral arithmetic.
    /// </summary>
    Int64,

    /// <summary>
    /// Uses unsigned 64-bit integral arithmetic.
    /// </summary>
    UInt64,

    /// <summary>
    /// Uses IEEE 754 single-precision arithmetic.
    /// </summary>
    Single,

    /// <summary>
    /// Uses IEEE 754 double-precision arithmetic.
    /// </summary>
    Double,

    /// <summary>
    /// Uses decimal arithmetic.
    /// </summary>
    Decimal
}
