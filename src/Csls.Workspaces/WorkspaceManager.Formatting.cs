using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;
using LspFormattingOptions = Csls.Protocol.FormattingOptions;
using LspRange = Csls.Protocol.Range;
using LspTextEdit = Csls.Protocol.TextEdit;
using RoslynFormattingOptions = Microsoft.CodeAnalysis.Formatting.FormattingOptions;

namespace Csls.Workspaces;

public sealed partial class WorkspaceManager
{
    /// <summary>
    /// Formats a complete document using Roslyn and the editor's indentation preferences.
    /// </summary>
    /// <param name="parameters">The target document and formatting preferences.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded non-overlapping document edits.</returns>
    public async Task<IReadOnlyList<LspTextEdit>> GetFormattingEditsAsync(
        DocumentFormattingParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateFormattingOptions(parameters.Options);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            SourceText? originalRazorText = await GetRazorTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (originalRazorText is null)
            {
                return [];
            }

            SourceText formattedRazorText = WorkspaceRazorFormattingService.Format(
                originalRazorText,
                path,
                parameters.Options,
                cancellationToken);
            formattedRazorText = ApplyFinalFormattingOptions(
                formattedRazorText,
                parameters.Options);
            return CreateTextEdits(
                originalRazorText,
                formattedRazorText.GetTextChanges(originalRazorText));
        }

        Document? document = FindDocument(parameters.TextDocument.Uri);
        if (document is null)
        {
            return [];
        }

        OptionSet options = CreateFormattingOptions(document, parameters.Options);
        Document formattedDocument = await Formatter.FormatAsync(
            document,
            options,
            cancellationToken).ConfigureAwait(false);
        SourceText originalText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        SourceText formattedText = await formattedDocument.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        formattedText = ApplyFinalFormattingOptions(formattedText, parameters.Options);
        return CreateTextEdits(originalText, formattedText.GetTextChanges(originalText));
    }

    /// <summary>
    /// Formats a document before save using project settings and stable Razor defaults.
    /// </summary>
    /// <param name="textDocument">The document that the editor is about to save.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded non-overlapping save-time edits.</returns>
    public async Task<IReadOnlyList<LspTextEdit>> GetSaveFormattingEditsAsync(
        TextDocumentIdentifier textDocument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(textDocument);
        string path = textDocument.Uri.GetFileSystemPath();
        if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            SourceText? originalRazorText = await GetRazorTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (originalRazorText is null)
            {
                return [];
            }

            SourceText formattedRazorText = WorkspaceRazorFormattingService.Format(
                originalRazorText,
                path,
                CreateSaveFormattingOptions(),
                cancellationToken);
            return CreateTextEdits(
                originalRazorText,
                formattedRazorText.GetTextChanges(originalRazorText));
        }

        Document? document = FindDocument(textDocument.Uri);
        if (document is null)
        {
            return [];
        }

        Document formattedDocument = await Formatter.FormatAsync(
            document,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        SourceText originalText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        SourceText formattedText = await formattedDocument.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        return CreateTextEdits(originalText, formattedText.GetTextChanges(originalText));
    }

    private static LspFormattingOptions CreateSaveFormattingOptions() => new()
    {
        TabSize = 4,
        InsertSpaces = true
    };

    /// <summary>
    /// Formats whitespace within one document range using Roslyn or the Razor formatter.
    /// </summary>
    /// <param name="parameters">The target document range and formatting preferences.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded non-overlapping range formatting edits.</returns>
    public async Task<IReadOnlyList<LspTextEdit>> GetRangeFormattingEditsAsync(
        DocumentRangeFormattingParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateFormattingOptions(parameters.Options);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            SourceText? originalRazorText = await GetRazorTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (originalRazorText is null)
            {
                return [];
            }

            SourceText formattedRazorText = WorkspaceRazorFormattingService.Format(
                originalRazorText,
                path,
                parameters.Options,
                cancellationToken);
            return CreateRazorRangeFormattingEdits(
                originalRazorText,
                formattedRazorText,
                parameters.Range);
        }

        Document? document = FindDocument(parameters.TextDocument.Uri);
        if (document is null)
        {
            return [];
        }

        SourceText originalText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        int start = LspPositionConverter.GetOffset(originalText, parameters.Range.Start);
        int end = LspPositionConverter.GetOffset(originalText, parameters.Range.End);
        var span = TextSpan.FromBounds(start, end);
        Document formattedDocument = await Formatter.FormatAsync(
            document,
            span,
            CreateFormattingOptions(document, parameters.Options),
            cancellationToken).ConfigureAwait(false);
        SourceText formattedText = await formattedDocument.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        return CreateTextEdits(originalText, formattedText.GetTextChanges(originalText));
    }

    /// <summary>
    /// Formats the localized source lines around one supported editor trigger.
    /// </summary>
    /// <param name="parameters">The target position, trigger, and formatting preferences.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The localized non-overlapping formatting edits.</returns>
    public async Task<IReadOnlyList<LspTextEdit>> GetOnTypeFormattingEditsAsync(
        DocumentOnTypeFormattingParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateFormattingOptions(parameters.Options);
        if (!IsOnTypeFormattingTrigger(parameters.Character))
        {
            return [];
        }

        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            SourceText? originalRazorText = await GetRazorTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            if (originalRazorText is null)
            {
                return [];
            }

            (TextSpan _, int razorStartLine, int razorEndLine) = GetOnTypeFormattingSpan(
                originalRazorText,
                parameters.Position,
                parameters.Character);
            SourceText formattedRazorText = WorkspaceRazorFormattingService.Format(
                originalRazorText,
                path,
                parameters.Options,
                cancellationToken);
            return CreateLineFormattingEdits(
                originalRazorText,
                formattedRazorText,
                razorStartLine,
                razorEndLine);
        }

        Document? document = FindDocument(parameters.TextDocument.Uri);
        if (document is null)
        {
            return [];
        }

        SourceText originalText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        (TextSpan span, int startLine, int endLine) = GetOnTypeFormattingSpan(
            originalText,
            parameters.Position,
            parameters.Character);
        Document formattedDocument = await Formatter.FormatAsync(
            document,
            span,
            CreateFormattingOptions(document, parameters.Options),
            cancellationToken).ConfigureAwait(false);
        SourceText formattedText = await formattedDocument.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        return CreateLineFormattingEdits(
            originalText,
            formattedText,
            startLine,
            endLine);
    }

    private static void ValidateFormattingOptions(LspFormattingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TabSize is < 1 or > 32)
        {
            throw new InvalidDataException("Formatting tabSize must be between 1 and 32.");
        }
    }

    private async Task<SourceText?> GetRazorTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        SourceText? text = _razorDocuments.GetValueOrDefault(path);
        if (text is not null)
        {
            return text;
        }

        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders =
            _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex < 0)
        {
            return null;
        }

        ImmutableArray<DocumentId> documentIds = folders[folderIndex]
            .Solution
            .GetDocumentIdsWithFilePath(path);
        for (int index = 0; index < documentIds.Length; index++)
        {
            TextDocument? document = folders[folderIndex]
                .Solution
                .GetAdditionalDocument(documentIds[index]);
            if (document is not null)
            {
                return await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static OptionSet CreateFormattingOptions(
        Document document,
        LspFormattingOptions options) =>
        document.Project.Solution.Options
            .WithChangedOption(
                RoslynFormattingOptions.UseTabs,
                LanguageNames.CSharp,
                !options.InsertSpaces)
            .WithChangedOption(
                RoslynFormattingOptions.TabSize,
                LanguageNames.CSharp,
                options.TabSize)
            .WithChangedOption(
                RoslynFormattingOptions.IndentationSize,
                LanguageNames.CSharp,
                options.TabSize);

    private static IReadOnlyList<LspTextEdit> CreateRazorRangeFormattingEdits(
        SourceText originalText,
        SourceText formattedText,
        LspRange range)
    {
        int start = LspPositionConverter.GetOffset(originalText, range.Start);
        int end = LspPositionConverter.GetOffset(originalText, range.End);
        int endProbe = end > start ? end - 1 : end;
        TextLine originalStartLine = originalText.Lines.GetLineFromPosition(start);
        TextLine originalEndLine = originalText.Lines.GetLineFromPosition(endProbe);
        return CreateLineFormattingEdits(
            originalText,
            formattedText,
            originalStartLine.LineNumber,
            originalEndLine.LineNumber);
    }

    private static IReadOnlyList<LspTextEdit> CreateLineFormattingEdits(
        SourceText originalText,
        SourceText formattedText,
        int startLineNumber,
        int endLineNumber)
    {
        if (formattedText.Lines.Count != originalText.Lines.Count)
        {
            throw new InvalidOperationException(
                "Localized formatting must preserve the document line count.");
        }

        TextLine originalStartLine = originalText.Lines[startLineNumber];
        TextLine originalEndLine = originalText.Lines[endLineNumber];
        TextLine formattedStartLine = formattedText.Lines[originalStartLine.LineNumber];
        TextLine formattedEndLine = formattedText.Lines[originalEndLine.LineNumber];
        var originalSpan = TextSpan.FromBounds(
            originalStartLine.Start,
            originalEndLine.End);
        var formattedSpan = TextSpan.FromBounds(
            formattedStartLine.Start,
            formattedEndLine.End);
        string newText = formattedText.ToString(formattedSpan);
        if (string.Equals(
            originalText.ToString(originalSpan),
            newText,
            StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            new LspTextEdit
            {
                Range = ToLspRange(originalText, originalSpan),
                NewText = newText
            }
        ];
    }

    private static bool IsOnTypeFormattingTrigger(string character) =>
        character is "}" or ";" or "\n";

    private static (TextSpan Span, int StartLine, int EndLine) GetOnTypeFormattingSpan(
        SourceText text,
        Position position,
        string character)
    {
        int offset = LspPositionConverter.GetOffset(text, position);
        TextLine currentLine = text.Lines.GetLineFromPosition(offset);
        int startLineNumber = character == "\n" && currentLine.LineNumber > 0
            ? currentLine.LineNumber - 1
            : currentLine.LineNumber;
        TextLine startLine = text.Lines[startLineNumber];
        return (
            TextSpan.FromBounds(startLine.Start, currentLine.End),
            startLineNumber,
            currentLine.LineNumber);
    }

    private static SourceText ApplyFinalFormattingOptions(
        SourceText text,
        LspFormattingOptions options)
    {
        string originalValue = text.ToString();
        string value = originalValue;
        string newline = GetPreferredNewline(text);
        if (options.TrimTrailingWhitespace is true)
        {
            var builder = new StringBuilder(value.Length);
            foreach (TextLine line in text.Lines)
            {
                int end = line.End;
                while (end > line.Start && char.IsWhiteSpace(value[end - 1]))
                {
                    end--;
                }

                builder.Append(value, line.Start, end - line.Start);
                builder.Append(value, line.End, line.EndIncludingLineBreak - line.End);
            }

            value = builder.ToString();
        }

        if (options.TrimFinalNewlines is true)
        {
            value = value.TrimEnd('\r', '\n');
        }

        if (options.InsertFinalNewline is true &&
            !value.EndsWith('\n') &&
            !value.EndsWith('\r'))
        {
            value += newline;
        }

        return string.Equals(value, originalValue, StringComparison.Ordinal)
            ? text
            : SourceText.From(value, text.Encoding, text.ChecksumAlgorithm);
    }

    private static string GetPreferredNewline(SourceText text)
    {
        foreach (TextLine line in text.Lines
            .Where(static line => line.EndIncludingLineBreak > line.End))
        {
            return text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
        }

        return Environment.NewLine;
    }
}
