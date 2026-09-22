namespace Csls.TestProcessHost;

/// <summary>
/// Distinguishes zero initialization from executing an explicit value-type constructor.
/// </summary>
internal struct DebuggerOptionalStructFixture
{
    /// <summary>
    /// Stores a primitive that remains zero under default initialization.
    /// </summary>
    internal readonly int _number;

    /// <summary>
    /// Stores a reference that remains null under default initialization.
    /// </summary>
    internal readonly string? _text;

    /// <summary>
    /// Initializes deliberately nondefault fields when the constructor executes.
    /// </summary>
    public DebuggerOptionalStructFixture()
    {
        _number = 41;
        _text = "constructed";
    }
}
