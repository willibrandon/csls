using Csls.Protocol;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Globalization;
using LspDiagnostic = Csls.Protocol.Diagnostic;
using LspDiagnosticSeverity = Csls.Protocol.DiagnosticSeverity;
using LspRange = Csls.Protocol.Range;
using RazorSeverity = Microsoft.AspNetCore.Razor.Language.RazorDiagnosticSeverity;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynDiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Csls.Workspaces;

/// <summary>
/// Parses current Razor documents and maps compiler syntax findings to LSP diagnostics.
/// </summary>
internal static class WorkspaceRazorDiagnosticService
{
    /// <summary>
    /// Determines whether a path identifies a Razor view or component document.
    /// </summary>
    /// <param name="path">The absolute document path.</param>
    /// <returns><see langword="true" /> for current Razor file kinds.</returns>
    internal static bool IsRazorDocument(string path) =>
        FileKinds.TryGetFileKindFromPath(path, out _);

    /// <summary>
    /// Combines Razor syntax findings with project-aware generated C# diagnostics.
    /// </summary>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="text">The immutable document text.</param>
    /// <param name="projectDiagnostics">Compiler and analyzer findings from owning projects.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exact ordered diagnostics for the current project snapshot.</returns>
    internal static IReadOnlyList<LspDiagnostic> GetDiagnostics(
        string path,
        SourceText text,
        IEnumerable<RoslynDiagnostic> projectDiagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectDiagnostics);
        RazorFileKind fileKind = FileKinds.GetFileKindFromPath(path);
        var parserOptions = RazorParserOptions.Create(
            RazorLanguageVersion.Latest,
            fileKind,
            static builder => builder.UseRoslynTokenizer = true);
        var source = RazorSourceDocument.Create(text.ToString(), path);
        var syntaxTree = RazorSyntaxTree.Parse(
            source,
            parserOptions,
            cancellationToken);
        var diagnostics = new List<LspDiagnostic>();
        var identities = new HashSet<(
            string? Code,
            Position Start,
            Position End,
            string Message)>();
        foreach (RazorDiagnostic diagnostic in syntaxTree.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddIfUnique(ToLspDiagnostic(diagnostic, text), diagnostics, identities);
        }

        foreach (RoslynDiagnostic diagnostic in projectDiagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryToLspDiagnostic(diagnostic, path, text, out LspDiagnostic? mappedDiagnostic) &&
                mappedDiagnostic is not null)
            {
                AddIfUnique(mappedDiagnostic, diagnostics, identities);
            }
        }

        diagnostics.Sort(static (left, right) =>
        {
            int result = left.Range.Start.Line.CompareTo(right.Range.Start.Line);
            if (result == 0)
            {
                result = left.Range.Start.Character.CompareTo(right.Range.Start.Character);
            }

            if (result == 0)
            {
                result = left.Range.End.Line.CompareTo(right.Range.End.Line);
            }

            if (result == 0)
            {
                result = left.Range.End.Character.CompareTo(right.Range.End.Character);
            }

            if (result == 0)
            {
                result = StringComparer.Ordinal.Compare(left.Code, right.Code);
            }

            if (result == 0)
            {
                result = StringComparer.Ordinal.Compare(left.Source, right.Source);
            }

            if (result == 0)
            {
                result = Nullable.Compare(left.Severity, right.Severity);
            }

            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(left.Message, right.Message);
        });
        return diagnostics;
    }

    private static void AddIfUnique(
        LspDiagnostic diagnostic,
        List<LspDiagnostic> diagnostics,
        HashSet<(string? Code, Position Start, Position End, string Message)> identities)
    {
        if (identities.Add((
            diagnostic.Code,
            diagnostic.Range.Start,
            diagnostic.Range.End,
            diagnostic.Message)))
        {
            diagnostics.Add(diagnostic);
        }
    }

    private static LspDiagnostic ToLspDiagnostic(
        RazorDiagnostic diagnostic,
        SourceText text)
    {
        int start = Math.Clamp(diagnostic.Span.AbsoluteIndex, 0, text.Length);
        int length = Math.Clamp(diagnostic.Span.Length, 0, text.Length - start);
        int end = start + length;
        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(
            TextSpan.FromBounds(start, end));
        return new LspDiagnostic
        {
            Range = new LspRange(
                new Position(
                    lineSpan.Start.Line,
                    lineSpan.Start.Character),
                new Position(
                    lineSpan.End.Line,
                    lineSpan.End.Character)),
            Severity = diagnostic.Severity switch
            {
                RazorSeverity.Error => LspDiagnosticSeverity.Error,
                RazorSeverity.Warning => LspDiagnosticSeverity.Warning,
                _ => null
            },
            Code = diagnostic.Id,
            Source = "Razor",
            Message = diagnostic.GetMessage(CultureInfo.InvariantCulture)
        };
    }

    private static bool TryToLspDiagnostic(
        RoslynDiagnostic diagnostic,
        string path,
        SourceText text,
        out LspDiagnostic? lspDiagnostic)
    {
        FileLinePositionSpan lineSpan = diagnostic.Location.GetMappedLineSpan();
        if (diagnostic.IsSuppressed ||
            !lineSpan.IsValid ||
            !string.Equals(lineSpan.Path, path, PathComparison))
        {
            lspDiagnostic = null;
            return false;
        }

        Position start = BoundPosition(lineSpan.StartLinePosition, text);
        Position end = BoundPosition(lineSpan.EndLinePosition, text);
        if (end.Line < start.Line ||
            end.Line == start.Line && end.Character < start.Character)
        {
            end = start;
        }

        lspDiagnostic = new LspDiagnostic
        {
            Range = new LspRange(start, end),
            Severity = diagnostic.Severity switch
            {
                RoslynDiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
                RoslynDiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
                RoslynDiagnosticSeverity.Info => LspDiagnosticSeverity.Information,
                RoslynDiagnosticSeverity.Hidden => LspDiagnosticSeverity.Hint,
                _ => null
            },
            Code = diagnostic.Id,
            Source = diagnostic.Id.StartsWith("RZ", StringComparison.Ordinal)
                ? "Razor"
                : diagnostic.Id.StartsWith("CS", StringComparison.Ordinal)
                    ? "C#"
                    : diagnostic.Descriptor.Category,
            Message = diagnostic.GetMessage(CultureInfo.InvariantCulture)
        };
        return true;
    }

    private static Position BoundPosition(LinePosition position, SourceText text)
    {
        int lineIndex = Math.Clamp(position.Line, 0, text.Lines.Count - 1);
        TextLine line = text.Lines[lineIndex];
        int character = Math.Clamp(position.Character, 0, line.End - line.Start);
        return new Position(lineIndex, character);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
