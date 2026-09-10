namespace Csls.TestProcessHost;

/// <summary>
/// Retains inline-only constructed structs and distinct string shapes for captured storage inspection.
/// </summary>
internal sealed class DebuggerDumpNestedFields
{
    /// <summary>
    /// Gets a nested constructed value retained exclusively inside this object.
    /// </summary>
    public readonly KeyValuePair<int, (long, string)> Pair = new(123, (456L, "inline-only"));

    /// <summary>
    /// Gets an empty captured string.
    /// </summary>
    public readonly string Empty = string.Empty;

    /// <summary>
    /// Gets captured UTF-16 code units requiring distinct debugger escaping.
    /// </summary>
    public readonly string Escaped = "before\0\n\ud800after";
}
