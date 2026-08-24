namespace Csls.Server;

/// <summary>
/// Defines settings that change language-server behavior for one LSP session.
/// </summary>
public sealed record LanguageServerConfiguration
{
    /// <summary>
    /// Gets whether project analyzers contribute document diagnostics.
    /// </summary>
    public bool EnableAnalyzers { get; init; } = true;

    /// <summary>
    /// Gets whether the server returns document formatting edits before a save.
    /// </summary>
    public bool FormatOnSave { get; init; }
}
