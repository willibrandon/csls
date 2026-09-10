namespace Csls.TestProcessHost;

/// <summary>
/// Provides inherited cross-module runtime fields for debugger expansion tests.
/// </summary>
internal sealed class DebuggerFixtureList : List<int>
{
    /// <summary>
    /// Initializes the list with one stable debugger-visible value.
    /// </summary>
    /// <param name="value">The initial list value.</param>
    internal DebuggerFixtureList(int value) => Add(value);
}
