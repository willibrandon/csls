using Csls.Protocol;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis.Text;
using System.Globalization;
using LspDiagnostic = Csls.Protocol.Diagnostic;
using LspDiagnosticSeverity = Csls.Protocol.DiagnosticSeverity;
using LspRange = Csls.Protocol.Range;
using RazorSeverity = Microsoft.AspNetCore.Razor.Language.RazorDiagnosticSeverity;

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
    /// Parses one immutable Razor snapshot and returns its ordered syntax diagnostics.
    /// </summary>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="text">The immutable document text.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exact Razor syntax diagnostics for the snapshot.</returns>
    internal static IReadOnlyList<LspDiagnostic> GetDiagnostics(
        string path,
        SourceText text,
        CancellationToken cancellationToken)
    {
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
        return
        [
            .. syntaxTree.Diagnostics
                .OrderBy(static diagnostic => diagnostic.Span.AbsoluteIndex)
                .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .Select(diagnostic => ToLspDiagnostic(diagnostic, text))
        ];
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
}
