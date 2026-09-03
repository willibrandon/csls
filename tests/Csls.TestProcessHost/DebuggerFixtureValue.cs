namespace Csls.TestProcessHost;

/// <summary>
/// Provides stable instance fields for debugger expansion tests.
/// </summary>
internal sealed class DebuggerFixtureValue
{
    /// <summary>
    /// Initializes the stable debugger value.
    /// </summary>
    /// <param name="number">The numeric field value.</param>
    /// <param name="text">The textual field value.</param>
    internal DebuggerFixtureValue(int number, string text)
    {
        Number = number;
        Text = text;
    }

    /// <summary>
    /// Gets the numeric field value.
    /// </summary>
    internal int Number;

    /// <summary>
    /// Gets the textual field value.
    /// </summary>
    internal string Text;
}
