using Microsoft.CodeAnalysis;

namespace Csls.Workspaces;

/// <summary>
/// Carries one design-time build diagnostic across the process boundary.
/// </summary>
internal sealed class MSBuildBuildHostDiagnostic
{
    /// <summary>
    /// Initializes one design-time build diagnostic.
    /// </summary>
    /// <param name="kind">The diagnostic severity.</param>
    /// <param name="message">The diagnostic message.</param>
    public MSBuildBuildHostDiagnostic(WorkspaceDiagnosticKind kind, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Kind = kind;
        Message = message;
    }

    /// <summary>
    /// Gets the diagnostic severity.
    /// </summary>
    public WorkspaceDiagnosticKind Kind { get; }

    /// <summary>
    /// Gets the diagnostic message.
    /// </summary>
    public string Message { get; }
}
