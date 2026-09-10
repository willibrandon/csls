namespace Csls.TestProcessHost;

/// <summary>
/// Exposes a nontrivial property getter used by managed step-filtering tests.
/// </summary>
internal sealed class DebuggerStepFilteringValue
{
    private readonly int _value;

    /// <summary>
    /// Creates a property owner for the supplied value.
    /// </summary>
    /// <param name="value">The value returned with one added.</param>
    internal DebuggerStepFilteringValue(int value)
    {
        _value = value;
    }

    /// <summary>
    /// Gets the stored value increased by one.
    /// </summary>
    internal int Answer
    {
        get
        {
            int answer = _value + 1;
            return answer;
        }
    }
}
