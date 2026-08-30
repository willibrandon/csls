using Microsoft.CodeAnalysis.CodeActions;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Csls.Workspaces;

/// <summary>
/// Holds one Roslyn code action and the diagnostics it fixes.
/// </summary>
internal sealed class RoslynCodeFix
{
    /// <summary>
    /// Initializes a new Roslyn code-fix result.
    /// </summary>
    internal RoslynCodeFix(
        CodeAction action,
        IReadOnlyList<RoslynDiagnostic> diagnostics)
    {
        Action = action;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets the Roslyn action.
    /// </summary>
    internal CodeAction Action { get; }

    /// <summary>
    /// Gets the diagnostics fixed by the action.
    /// </summary>
    internal IReadOnlyList<RoslynDiagnostic> Diagnostics { get; }
}
