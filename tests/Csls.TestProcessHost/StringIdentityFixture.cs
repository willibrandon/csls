using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Keeps physical string identities distinct from colliding debugger display labels.
/// </summary>
[DebuggerDisplay("123", Type = "int")]
internal sealed class StringIdentityFixture
{
    /// <summary>
    /// Stores the first string while presenting it under the second field's source name.
    /// </summary>
    [DebuggerDisplay("display-only", Name = "_second", Type = "display-string")]
    internal string _first = string.Empty;

    /// <summary>
    /// Stores the distinct string whose actual name collides with the first display label.
    /// </summary>
    internal string _second = string.Empty;

    /// <summary>
    /// Retains actual string array elements for direct reference assignment.
    /// </summary>
    internal readonly string[] _items = ["unused", "array\\value"];

    /// <summary>
    /// Updates both physical strings through the fixture's ordinary mutable application state.
    /// </summary>
    /// <param name="first">The first physical string value.</param>
    /// <param name="second">The second physical string value.</param>
    internal void SetValues(string first, string second)
    {
        _first = first;
        _second = second;
    }
}
