using Microsoft.Extensions.Logging;

namespace Csls.Server;

/// <summary>
/// Holds configuration values supplied by one optional client section.
/// </summary>
internal sealed record LanguageServerConfigurationPatch
{
    /// <summary>
    /// Gets the optional analyzer setting.
    /// </summary>
    public bool? EnableAnalyzers { get; init; }

    /// <summary>
    /// Gets the optional save-formatting setting.
    /// </summary>
    public bool? FormatOnSave { get; init; }

    /// <summary>
    /// Gets the optional parameter-hint setting.
    /// </summary>
    public bool? EnableInlayHintsForParameters { get; init; }

    /// <summary>
    /// Gets the optional type-hint setting.
    /// </summary>
    public bool? EnableInlayHintsForTypes { get; init; }

    /// <summary>
    /// Gets the optional informational-diagnostic presentation setting.
    /// </summary>
    public bool? ReportInformationAsHint { get; init; }

    /// <summary>
    /// Gets the optional closed-file workspace diagnostic setting.
    /// </summary>
    public bool? EnableWorkspaceDiagnostics { get; init; }

    /// <summary>
    /// Gets the optional MSBuild configuration.
    /// </summary>
    public string? BuildConfiguration { get; init; }

    /// <summary>
    /// Gets the optional server log level.
    /// </summary>
    public LogLevel? LogLevel { get; init; }
}
