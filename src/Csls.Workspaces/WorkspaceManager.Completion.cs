using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;
using LspCompletionContext = Csls.Protocol.CompletionContext;
using LspCompletionItem = Csls.Protocol.CompletionItem;
using LspCompletionItemKind = Csls.Protocol.CompletionItemKind;
using LspCompletionList = Csls.Protocol.CompletionList;
using LspCompletionTriggerKind = Csls.Protocol.CompletionTriggerKind;
using LspRange = Csls.Protocol.Range;
using LspTextEdit = Csls.Protocol.TextEdit;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;

namespace Csls.Workspaces;

/// <summary>
/// Exposes completion and completion-resolution operations for workspace snapshots.
/// </summary>
public sealed partial class WorkspaceManager
{
    private const int MaximumCompletionItems = 200;

    /// <summary>
    /// Gets bounded Roslyn completion candidates and exact commit edits for one document position.
    /// </summary>
    /// <param name="parameters">The document position and optional completion trigger.</param>
    /// <param name="supportsSnippets">Whether the client supports LSP snippet insertion text.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The ordered completion candidates.</returns>
    public async Task<LspCompletionList> GetCompletionsAsync(
        CompletionParams parameters,
        bool supportsSnippets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string path = parameters.TextDocument.Uri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex < 0)
        {
            return new LspCompletionList { Items = [], IsIncomplete = false };
        }

        (Document? document, SourceText? text, int offset, RazorMappedDocument? razorMapping) =
            await ResolveCompletionDocumentAsync(
                folders[folderIndex].Solution,
                path,
                parameters.Position,
                cancellationToken).ConfigureAwait(false);
        if (document is null || text is null)
        {
            return new LspCompletionList { Items = [], IsIncomplete = false };
        }

        var service = CompletionService.GetService(document);
        if (service is null)
        {
            return new LspCompletionList { Items = [], IsIncomplete = false };
        }

        CompletionTrigger trigger = CreateCompletionTrigger(parameters.Context);
        RoslynCompletionList? completion = await service
            .GetCompletionsAsync(
                document,
                offset,
                trigger: trigger,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (completion is null)
        {
            return new LspCompletionList { Items = [], IsIncomplete = false };
        }

        IReadOnlyList<RoslynCompletionItem> sourceItems = OrderCompletionItems(
            completion,
            text);
        int itemCount = Math.Min(sourceItems.Count, MaximumCompletionItems);
        var items = new List<LspCompletionItem>(itemCount);
        long generation = Generation;
        for (int index = 0; index < itemCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoslynCompletionItem sourceItem = sourceItems[index];
            CompletionChange change = await service
                .GetChangeAsync(document, sourceItem, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            LspCompletionItem? item = CreateCompletionItem(
                text,
                sourceItem,
                change,
                parameters,
                generation,
                index,
                supportsSnippets,
                razorMapping,
                cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return new LspCompletionList
        {
            IsIncomplete = sourceItems.Count > MaximumCompletionItems || items.Count < itemCount,
            Items = items
        };
    }

    /// <summary>
    /// Resolves Roslyn documentation for one completion candidate without changing its edits.
    /// </summary>
    /// <param name="item">The completion candidate returned by this workspace generation.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <param name="supportsMarkdown">Whether the receiving client accepts Markdown.</param>
    /// <returns>The completion candidate enriched with documentation.</returns>
    public async Task<LspCompletionItem> ResolveCompletionAsync(
        LspCompletionItem item,
        CancellationToken cancellationToken,
        bool supportsMarkdown = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        CompletionItemData data = item.Data
            ?? throw new InvalidOperationException("The completion item has no resolve data.");
        if (data.WorkspaceGeneration != Generation)
        {
            throw new InvalidOperationException(
                "The workspace changed after the completion item was produced.");
        }

        string path = data.DocumentUri.GetFileSystemPath();
        ImmutableArray<(string RootPath, Workspace Workspace, Solution Solution)> folders = _folders;
        int folderIndex = FindFolderIndex(path, folders);
        if (folderIndex < 0)
        {
            throw new InvalidOperationException("The completion document is no longer loaded.");
        }

        (Document? document, SourceText? text, int offset, _) =
            await ResolveCompletionDocumentAsync(
                folders[folderIndex].Solution,
                path,
                data.Position,
                cancellationToken).ConfigureAwait(false);
        if (document is null || text is null)
        {
            throw new InvalidOperationException("The completion document is no longer loaded.");
        }

        CompletionService service = CompletionService.GetService(document)
            ?? throw new InvalidOperationException("Roslyn completion is unavailable.");
        RoslynCompletionList? completion = await service
            .GetCompletionsAsync(
                document,
                offset,
                trigger: CreateCompletionTrigger(data.Context),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (completion is null)
        {
            throw new InvalidOperationException("Roslyn no longer returns this completion list.");
        }

        IReadOnlyList<RoslynCompletionItem> sourceItems = OrderCompletionItems(
            completion,
            text);
        if ((uint)data.ItemIndex >= (uint)sourceItems.Count)
        {
            throw new InvalidOperationException("The completion item index is no longer valid.");
        }

        RoslynCompletionItem sourceItem = sourceItems[data.ItemIndex];
        if (!MatchesCompletionItem(sourceItem, data))
        {
            throw new InvalidOperationException("The completion item no longer matches Roslyn state.");
        }

        CompletionDescription? description = await service
            .GetDescriptionAsync(document, sourceItem, cancellationToken)
            .ConfigureAwait(false);
        MarkupContent? documentation = description is null ||
            description.TaggedParts.IsDefaultOrEmpty
                ? null
                : TaggedTextMarkupFormatter.Format(
                    description.TaggedParts,
                    supportsMarkdown);
        (ISymbol? documentationSymbol, Compilation? compilation) =
            await ResolveCompletionSymbolAsync(
                document,
                sourceItem,
                offset,
                cancellationToken).ConfigureAwait(false);
        if (documentationSymbol is not null && compilation is not null)
        {
            MarkupContent? supplemental = SymbolDocumentationFormatter
                .FormatSymbol(
                    documentationSymbol,
                    compilation,
                    supportsMarkdown,
                    cancellationToken)
                .SupplementalDocumentation;
            documentation = documentation is null
                ? supplemental
                : TaggedTextMarkupFormatter.Combine(documentation, supplemental);
        }

        return documentation is null || string.IsNullOrEmpty(documentation.Value)
            ? item
            : item with
            {
                Documentation = documentation
            };
    }

    private static async Task<(ISymbol? Symbol, Compilation? Compilation)>
        ResolveCompletionSymbolAsync(
            Document document,
            RoslynCompletionItem item,
            int offset,
            CancellationToken cancellationToken)
    {
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        string name = item.Properties.TryGetValue("SymbolName", out string? symbolName)
            ? symbolName
            : item.DisplayText;
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        int tokenOffset = Math.Clamp(offset - 1, 0, Math.Max(0, root.FullSpan.End - 1));
        MemberAccessExpressionSyntax? memberAccess = root
            .FindToken(tokenOffset, findInsideTrivia: true)
            .Parent?
            .AncestorsAndSelf()
            .OfType<MemberAccessExpressionSyntax>()
            .FirstOrDefault();
        if (memberAccess is not null)
        {
            ITypeSymbol? receiverType = semanticModel
                .GetTypeInfo(memberAccess.Expression, cancellationToken)
                .Type;
            if (receiverType is not null)
            {
                ISymbol? member = SelectDocumentedSymbol(
                    semanticModel.LookupSymbols(
                        offset,
                        receiverType,
                        name,
                        includeReducedExtensionMethods: true),
                    cancellationToken);
                if (member is not null)
                {
                    return (member, semanticModel.Compilation);
                }
            }
        }

        ISymbol? visibleSymbol = SelectDocumentedSymbol(
            semanticModel.LookupSymbols(
                offset,
                name: name,
                includeReducedExtensionMethods: true),
            cancellationToken);
        if (visibleSymbol is not null)
        {
            return (visibleSymbol, semanticModel.Compilation);
        }

        ISymbol? compilationSymbol = SelectDocumentedSymbol(
            semanticModel.Compilation.GetSymbolsWithName(
                name,
                SymbolFilter.TypeAndMember,
                cancellationToken),
            cancellationToken);
        return (compilationSymbol, semanticModel.Compilation);
    }

    private static ISymbol? SelectDocumentedSymbol(
        IEnumerable<ISymbol> symbols,
        CancellationToken cancellationToken)
    {
        ISymbol? first = null;
        foreach (ISymbol symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            first ??= symbol;
            if (!string.IsNullOrWhiteSpace(symbol.GetDocumentationCommentXml(
                expandIncludes: true,
                cancellationToken: cancellationToken)))
            {
                return symbol;
            }
        }

        return first;
    }

    private static LspCompletionItem? CreateCompletionItem(
        SourceText sourceText,
        RoslynCompletionItem sourceItem,
        CompletionChange change,
        CompletionParams parameters,
        long generation,
        int itemIndex,
        bool supportsSnippets,
        RazorMappedDocument? razorMapping,
        CancellationToken cancellationToken)
    {
        ImmutableArray<TextChange> textChanges = change.TextChanges.IsDefaultOrEmpty
            ? [change.TextChange]
            : change.TextChanges;
        int primaryIndex = 0;
        for (int index = 0; index < textChanges.Length; index++)
        {
            TextSpan span = textChanges[index].Span;
            if (span == sourceItem.Span || span.IntersectsWith(sourceItem.Span))
            {
                primaryIndex = index;
                break;
            }
        }

        LspTextEdit? primaryEdit = ToTextEdit(
            sourceText,
            textChanges[primaryIndex],
            razorMapping,
            cancellationToken);
        if (primaryEdit is null)
        {
            return null;
        }

        bool usesSnippet = supportsSnippets &&
            sourceItem.Tags.Contains(WellKnownTags.Snippet);
        if (usesSnippet)
        {
            primaryEdit = primaryEdit with
            {
                NewText = CreateSnippetText(change, textChanges, primaryIndex)
            };
        }

        var additionalEdits = new List<LspTextEdit>(textChanges.Length - 1);
        for (int index = 0; index < textChanges.Length; index++)
        {
            if (index == primaryIndex)
            {
                continue;
            }

            LspTextEdit? edit = ToTextEdit(
                sourceText,
                textChanges[index],
                razorMapping,
                cancellationToken);
            if (edit is null)
            {
                return null;
            }

            additionalEdits.Add(edit);
        }

        string label = GetCompletionLabel(sourceItem);
        return new LspCompletionItem
        {
            Label = label,
            Kind = GetCompletionKind(sourceItem.Tags),
            Detail = string.IsNullOrWhiteSpace(sourceItem.InlineDescription)
                ? null
                : sourceItem.InlineDescription,
            SortText = sourceItem.SortText,
            FilterText = sourceItem.FilterText,
            TextEdit = primaryEdit,
            AdditionalTextEdits = additionalEdits.Count == 0 ? null : additionalEdits,
            InsertTextFormat = usesSnippet ? InsertTextFormat.Snippet : null,
            Data = new CompletionItemData
            {
                DocumentUri = parameters.TextDocument.Uri,
                Position = parameters.Position,
                Context = parameters.Context,
                WorkspaceGeneration = generation,
                ItemIndex = itemIndex,
                Label = label,
                SortText = sourceItem.SortText,
                FilterText = sourceItem.FilterText,
                SpanStart = sourceItem.Span.Start,
                SpanLength = sourceItem.Span.Length
            }
        };
    }

    private static CompletionTrigger CreateCompletionTrigger(LspCompletionContext? context) =>
        context is
        {
            TriggerKind: LspCompletionTriggerKind.TriggerCharacter,
            TriggerCharacter.Length: 1
        }
            ? CompletionTrigger.CreateInsertionTrigger(context.TriggerCharacter[0])
            : CompletionTrigger.Invoke;

    private static IReadOnlyList<RoslynCompletionItem> OrderCompletionItems(
        RoslynCompletionList completion,
        SourceText text)
    {
        string filterText = text.ToString(completion.Span);
        return
        [
            .. completion.ItemsList
                .Select(static (item, index) => (Item: item, Index: index))
                .OrderBy(candidate => GetCompletionMatchRank(candidate.Item, filterText))
                .ThenBy(static candidate => candidate.Index)
                .Select(static candidate => candidate.Item)
        ];
    }

    private static bool MatchesCompletionItem(
        RoslynCompletionItem item,
        CompletionItemData data) =>
        string.Equals(GetCompletionLabel(item), data.Label, StringComparison.Ordinal) &&
        string.Equals(item.SortText, data.SortText, StringComparison.Ordinal) &&
        string.Equals(item.FilterText, data.FilterText, StringComparison.Ordinal) &&
        item.Span.Start == data.SpanStart &&
        item.Span.Length == data.SpanLength;

    private static string GetCompletionLabel(RoslynCompletionItem item) =>
        string.Concat(
            item.DisplayTextPrefix,
            item.DisplayText,
            item.DisplayTextSuffix);

    private static string CreateSnippetText(
        CompletionChange change,
        ImmutableArray<TextChange> textChanges,
        int primaryIndex)
    {
        TextChange primaryChange = textChanges[primaryIndex];
        string text = primaryChange.NewText ?? string.Empty;
        int newStart = primaryChange.Span.Start;
        foreach (TextChange precedingChange in textChanges)
        {
            if (precedingChange.Span.Start >= primaryChange.Span.Start)
            {
                continue;
            }

            newStart += (precedingChange.NewText?.Length ?? 0) - precedingChange.Span.Length;
        }

        int caretOffset = change.NewPosition is int newPosition
            ? newPosition - newStart
            : text.Length;
        if ((uint)caretOffset > (uint)text.Length)
        {
            caretOffset = text.Length;
        }

        var snippet = new StringBuilder(text.Length + 2);
        AppendEscapedSnippetText(snippet, text.AsSpan(0, caretOffset));
        snippet.Append("$0");
        AppendEscapedSnippetText(snippet, text.AsSpan(caretOffset));
        return snippet.ToString();
    }

    private static void AppendEscapedSnippetText(
        StringBuilder destination,
        ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (character is '\\' or '$' or '}')
            {
                destination.Append('\\');
            }

            destination.Append(character);
        }
    }

    private static LspTextEdit? ToTextEdit(
        SourceText sourceText,
        TextChange change,
        RazorMappedDocument? razorMapping,
        CancellationToken cancellationToken)
    {
        if (razorMapping is not null)
        {
            if (WorkspaceRazorMappingService.TryMapRange(
                razorMapping,
                change.Span,
                cancellationToken,
                out LspRange range))
            {
                return new LspTextEdit
                {
                    Range = range,
                    NewText = change.NewText ?? string.Empty
                };
            }

            return WorkspaceRazorCompletionEditService.TryCreateUsingEdit(
                razorMapping,
                change,
                out LspTextEdit edit)
                ? edit
                : null;
        }

        return new LspTextEdit
        {
            Range = GetRange(sourceText, change.Span),
            NewText = change.NewText ?? string.Empty
        };
    }

    private static async Task<(
        Document? Document,
        SourceText? Text,
        int Offset,
        RazorMappedDocument? RazorMapping)> ResolveCompletionDocumentAsync(
            Solution solution,
            string path,
            Position position,
            CancellationToken cancellationToken)
    {
        if (WorkspaceRazorDiagnosticService.IsRazorDocument(path))
        {
            RazorMappedDocument? mapping = await WorkspaceRazorMappingService.ResolveAsync(
                solution,
                path,
                position,
                cancellationToken).ConfigureAwait(false);
            return mapping is null
                ? (null, null, 0, null)
                : (
                    mapping.Document,
                    await mapping.Document.GetTextAsync(cancellationToken).ConfigureAwait(false),
                    mapping.GeneratedOffset,
                    mapping);
        }

        Document? document = FindDocument(solution, path);
        if (document is null)
        {
            return (null, null, 0, null);
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        return (
            document,
            text,
            LspPositionConverter.GetOffset(text, position),
            null);
    }

    private static LspRange GetRange(SourceText text, TextSpan span)
    {
        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange(
            new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            new Position(lineSpan.End.Line, lineSpan.End.Character));
    }

    private static LspCompletionItemKind GetCompletionKind(ImmutableArray<string> tags)
    {
        if (tags.Contains(WellKnownTags.Method))
        {
            return LspCompletionItemKind.Method;
        }

        if (tags.Contains(WellKnownTags.ExtensionMethod))
        {
            return LspCompletionItemKind.Method;
        }

        if (tags.Contains(WellKnownTags.Property))
        {
            return LspCompletionItemKind.Property;
        }

        if (tags.Contains(WellKnownTags.Field))
        {
            return LspCompletionItemKind.Field;
        }

        if (tags.Contains(WellKnownTags.Event))
        {
            return LspCompletionItemKind.Event;
        }

        if (tags.Contains(WellKnownTags.Class))
        {
            return LspCompletionItemKind.Class;
        }

        if (tags.Contains(WellKnownTags.Structure))
        {
            return LspCompletionItemKind.Struct;
        }

        if (tags.Contains(WellKnownTags.Interface))
        {
            return LspCompletionItemKind.Interface;
        }

        if (tags.Contains(WellKnownTags.EnumMember))
        {
            return LspCompletionItemKind.EnumMember;
        }

        if (tags.Contains(WellKnownTags.Enum))
        {
            return LspCompletionItemKind.Enum;
        }

        if (tags.Contains(WellKnownTags.Constant))
        {
            return LspCompletionItemKind.Constant;
        }

        if (tags.Contains(WellKnownTags.Namespace) || tags.Contains(WellKnownTags.Module))
        {
            return LspCompletionItemKind.Module;
        }

        if (tags.Contains(WellKnownTags.TypeParameter))
        {
            return LspCompletionItemKind.TypeParameter;
        }

        if (tags.Contains(WellKnownTags.Keyword))
        {
            return LspCompletionItemKind.Keyword;
        }

        if (tags.Contains(WellKnownTags.Snippet))
        {
            return LspCompletionItemKind.Snippet;
        }

        if (tags.Contains(WellKnownTags.Local) ||
            tags.Contains(WellKnownTags.Parameter) ||
            tags.Contains(WellKnownTags.RangeVariable))
        {
            return LspCompletionItemKind.Variable;
        }

        return LspCompletionItemKind.Text;
    }

    private static int GetCompletionMatchRank(
        RoslynCompletionItem item,
        string filterText)
    {
        if (filterText.Length == 0)
        {
            return 0;
        }

        if (string.Equals(item.FilterText, filterText, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (item.FilterText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return item.FilterText.Contains(filterText, StringComparison.OrdinalIgnoreCase)
            ? 2
            : 3;
    }
}
