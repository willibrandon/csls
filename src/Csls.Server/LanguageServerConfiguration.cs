using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Gets the MSBuild configuration used to evaluate loaded projects.
    /// </summary>
    public string BuildConfiguration { get; init; } = "Debug";

    /// <summary>
    /// Gets the minimum level written by language-server logging providers.
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Information;
}
