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

    /// <summary>
    /// Reads the value through an instance call on unboxed struct storage.
    /// </summary>
    /// <returns>The current stored number.</returns>
    internal int ReadNumber() => _number;

    /// <summary>
    /// Echoes an inferred method argument from an unboxed value-type receiver.
    /// </summary>
    /// <typeparam name="T">The inferred argument and result type.</typeparam>
    /// <param name="value">The value supplied through debugger evaluation.</param>
    /// <returns>The original value.</returns>
    internal T EchoGeneric<T>(T value)
    {
        if (_number < 0)
        {
            throw new InvalidOperationException("The debugger fixture has invalid storage.");
        }

        return value;
    }
}
