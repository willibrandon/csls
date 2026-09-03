namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one source document represented by loaded debugger symbols.
/// </summary>
/// <param name="Name">The source document display name.</param>
/// <param name="Path">The normalized source document path.</param>
public sealed record DebugSourceInfo(string Name, string Path);
