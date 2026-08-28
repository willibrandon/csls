using Csls.Protocol;
using Microsoft.CodeAnalysis;
using LspDiagnosticSeverity = Csls.Protocol.DiagnosticSeverity;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Csls.Workspaces;

/// <summary>
/// Applies Roslyn's editor-facing policy for hidden and fading diagnostics.
/// </summary>
internal static class WorkspaceDiagnosticPolicy
{
    private const string UnnecessaryUsingDirectiveId = "CS8019";

    /// <summary>
    /// Determines whether a Roslyn diagnostic can be represented by a standard LSP client.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to inspect.</param>
    /// <returns>True for visible diagnostics and hidden diagnostics intended for fading.</returns>
    internal static bool ShouldInclude(RoslynDiagnostic diagnostic) =>
        diagnostic.Severity != RoslynDiagnosticSeverity.Hidden || IsUnnecessary(diagnostic);

    /// <summary>
    /// Gets the standard LSP tags associated with a Roslyn diagnostic.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to inspect.</param>
    /// <returns>The diagnostic tags, or null when no standard tag applies.</returns>
    internal static IReadOnlyList<DiagnosticTag>? GetTags(RoslynDiagnostic diagnostic) =>
        IsUnnecessary(diagnostic) ? [DiagnosticTag.Unnecessary] : null;

    /// <summary>
    /// Gets the editor-facing LSP severity for one Roslyn diagnostic.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to convert.</param>
    /// <param name="reportInformationAsHint">Whether information is presented as a hint.</param>
    /// <returns>The configured LSP severity.</returns>
    internal static LspDiagnosticSeverity? GetSeverity(
        RoslynDiagnostic diagnostic,
        bool reportInformationAsHint) => diagnostic.Severity switch
        {
            RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
            RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
            RoslynDiagnosticSeverity.Info when reportInformationAsHint =>
                LspDiagnosticSeverity.Hint,
            RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Information,
            RoslynDiagnosticSeverity.Hidden => LspDiagnosticSeverity.Hint,
            _ => null
        };

    private static bool IsUnnecessary(RoslynDiagnostic diagnostic) =>
        string.Equals(diagnostic.Id, UnnecessaryUsingDirectiveId, StringComparison.Ordinal) ||
        diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary);
}
