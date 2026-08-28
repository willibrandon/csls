using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Computes and resolves bounded C# inlay hints from immutable Roslyn snapshots.
/// </summary>
internal static class WorkspaceInlayHintService
{
    private const int MaximumInlayHints = 2_000;

    /// <summary>
    /// Computes inferred local-type and argument parameter-name hints in one range.
    /// </summary>
    /// <param name="document">The current immutable source document.</param>
    /// <param name="range">The visible UTF-16 document range.</param>
    /// <param name="generation">The captured workspace generation.</param>
    /// <param name="includeParameterHints">Whether parameter-name hints are enabled.</param>
    /// <param name="includeTypeHints">Whether inferred-type hints are enabled.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded ordered inlay hints.</returns>
    internal static async Task<IReadOnlyList<InlayHint>> GetInlayHintsAsync(
        Document? document,
        LspRange range,
        long generation,
        bool includeParameterHints,
        bool includeTypeHints,
        CancellationToken cancellationToken)
    {
        if (document is null || (!includeParameterHints && !includeTypeHints))
        {
            return [];
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        TextSpan requestedSpan = GetSpan(text, range);
        var uri = DocumentUri.FromFileSystemPath(
            document.FilePath
                ?? throw new InvalidOperationException("An inlay-hint document has no path."));
        var hints = new List<InlayHint>();
        if (includeTypeHints)
        {
            AddLocalTypeHints(
                root,
                semanticModel,
                text,
                requestedSpan,
                uri,
                generation,
                hints,
                cancellationToken);
        }

        if (includeParameterHints)
        {
            AddParameterNameHints(
                root,
                semanticModel,
                text,
                requestedSpan,
                uri,
                generation,
                hints,
                cancellationToken);
        }
        return
        [
            .. hints
                .OrderBy(static hint => hint.Position.Line)
                .ThenBy(static hint => hint.Position.Character)
                .ThenBy(static hint => hint.Kind)
                .Take(MaximumInlayHints)
        ];
    }

    /// <summary>
    /// Resolves semantic tooltip details for one previously returned hint.
    /// </summary>
    /// <param name="document">The current immutable source document.</param>
    /// <param name="hint">The server-produced hint to resolve.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The hint populated with a semantic tooltip.</returns>
    internal static async Task<InlayHint> ResolveAsync(
        Document document,
        InlayHint hint,
        CancellationToken cancellationToken)
    {
        InlayHintData data = hint.Data
            ?? throw new InvalidOperationException("The inlay hint has no resolve data.");
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no syntax root.");
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no semantic model.");
        TextSpan sourceSpan = GetSpan(text, data.SourceRange);
        return data.Kind switch
        {
            InlayHintDataKind.LocalType => ResolveLocalTypeHint(
                hint,
                root,
                semanticModel,
                sourceSpan,
                cancellationToken),
            InlayHintDataKind.ParameterName => ResolveParameterHint(
                hint,
                root,
                semanticModel,
                sourceSpan,
                cancellationToken),
            _ => throw new InvalidOperationException("The inlay hint resolve kind is unknown.")
        };
    }

    private static void AddLocalTypeHints(
        SyntaxNode root,
        SemanticModel semanticModel,
        SourceText text,
        TextSpan requestedSpan,
        DocumentUri uri,
        long generation,
        List<InlayHint> hints,
        CancellationToken cancellationToken)
    {
        foreach (VariableDeclarationSyntax declaration in root
            .DescendantNodes()
            .OfType<VariableDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declaration.Type.IsVar ||
                !requestedSpan.IntersectsWith(declaration.Span) ||
                declaration.Parent is not LocalDeclarationStatementSyntax and
                    not ForStatementSyntax and
                    not UsingStatementSyntax)
            {
                continue;
            }

            foreach (VariableDeclaratorSyntax variable in declaration.Variables)
            {
                var local = semanticModel.GetDeclaredSymbol(
                    variable,
                    cancellationToken) as ILocalSymbol;
                ITypeSymbol? inferredType = local is null
                    ? null
                    : GetInferredLocalType(
                        variable,
                        local,
                        semanticModel,
                        cancellationToken);
                if (inferredType is null ||
                    inferredType.TypeKind == TypeKind.Error ||
                    inferredType.IsAnonymousType)
                {
                    continue;
                }

                string typeName = inferredType.ToMinimalDisplayString(
                    semanticModel,
                    declaration.Type.SpanStart,
                    SymbolDisplayFormat.MinimallyQualifiedFormat);
                LspRange replacementRange = ToRange(text, declaration.Type.Span);
                hints.Add(new InlayHint
                {
                    Position = ToPosition(text, variable.Identifier.SpanStart),
                    Label = typeName,
                    Kind = InlayHintKind.Type,
                    TextEdits =
                    [
                        new TextEdit
                        {
                            Range = replacementRange,
                            NewText = typeName
                        }
                    ],
                    PaddingLeft = false,
                    PaddingRight = true,
                    Data = new InlayHintData
                    {
                        Generation = generation,
                        Uri = uri,
                        SourceRange = ToRange(text, variable.Span),
                        Kind = InlayHintDataKind.LocalType
                    }
                });
                if (hints.Count >= MaximumInlayHints)
                {
                    return;
                }
            }
        }
    }

    private static void AddParameterNameHints(
        SyntaxNode root,
        SemanticModel semanticModel,
        SourceText text,
        TextSpan requestedSpan,
        DocumentUri uri,
        long generation,
        List<InlayHint> hints,
        CancellationToken cancellationToken)
    {
        foreach (ArgumentSyntax argument in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (argument.NameColon is not null ||
                !requestedSpan.IntersectsWith(argument.Span) ||
                semanticModel.GetOperation(argument, cancellationToken) is not IArgumentOperation
                {
                    Parameter: not null
                } operation ||
                IsSelfEvident(argument.Expression, operation.Parameter.Name))
            {
                continue;
            }

            Position position = ToPosition(text, argument.SpanStart);
            string insertion = $"{operation.Parameter.Name}: ";
            hints.Add(new InlayHint
            {
                Position = position,
                Label = $"{operation.Parameter.Name}:",
                Kind = InlayHintKind.Parameter,
                TextEdits =
                [
                    new TextEdit
                    {
                        Range = new LspRange(position, position),
                        NewText = insertion
                    }
                ],
                PaddingLeft = false,
                PaddingRight = true,
                Data = new InlayHintData
                {
                    Generation = generation,
                    Uri = uri,
                    SourceRange = ToRange(text, argument.Span),
                    Kind = InlayHintDataKind.ParameterName
                }
            });
            if (hints.Count >= MaximumInlayHints)
            {
                return;
            }
        }
    }

    private static InlayHint ResolveLocalTypeHint(
        InlayHint hint,
        SyntaxNode root,
        SemanticModel semanticModel,
        TextSpan sourceSpan,
        CancellationToken cancellationToken)
    {
        VariableDeclaratorSyntax? variable = root
            .FindNode(sourceSpan, getInnermostNodeForTie: true)
            .FirstAncestorOrSelf<VariableDeclaratorSyntax>();
        ILocalSymbol? local = variable is null
            ? null
            : semanticModel.GetDeclaredSymbol(variable, cancellationToken) as ILocalSymbol;
        if (local is null)
        {
            throw new InvalidOperationException("The local variable for this inlay hint is unavailable.");
        }

        ITypeSymbol inferredType = GetInferredLocalType(
            variable!,
            local,
            semanticModel,
            cancellationToken);
        string display = inferredType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return hint with
        {
            Tooltip = new MarkupContent
            {
                Kind = "markdown",
                Value = $"Inferred type: `{display}`"
            }
        };
    }

    private static InlayHint ResolveParameterHint(
        InlayHint hint,
        SyntaxNode root,
        SemanticModel semanticModel,
        TextSpan sourceSpan,
        CancellationToken cancellationToken)
    {
        ArgumentSyntax? argument = root
            .FindNode(sourceSpan, getInnermostNodeForTie: true)
            .FirstAncestorOrSelf<ArgumentSyntax>();
        IParameterSymbol? parameter = argument is null
            ? null
            : (semanticModel.GetOperation(argument, cancellationToken) as IArgumentOperation)
                ?.Parameter;
        if (parameter is null)
        {
            throw new InvalidOperationException("The parameter for this inlay hint is unavailable.");
        }

        string parameterDisplay = parameter.ToDisplayString(
            SymbolDisplayFormat.MinimallyQualifiedFormat);
        string memberDisplay = parameter.ContainingSymbol.ToDisplayString(
            SymbolDisplayFormat.MinimallyQualifiedFormat);
        return hint with
        {
            Tooltip = new MarkupContent
            {
                Kind = "markdown",
                Value = $"Parameter `{parameterDisplay}` in `{memberDisplay}`"
            }
        };
    }

    private static bool IsSelfEvident(ExpressionSyntax expression, string parameterName)
    {
        string? expressionName = expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Identifier.ValueText,
            _ => null
        };
        return expressionName is not null && string.Equals(
            NormalizeName(expressionName),
            NormalizeName(parameterName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string value) => value.TrimStart('_');

    private static ITypeSymbol GetInferredLocalType(
        VariableDeclaratorSyntax variable,
        ILocalSymbol local,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        variable.Initializer is null
            ? local.Type
            : semanticModel.GetTypeInfo(
                variable.Initializer.Value,
                cancellationToken).Type ?? local.Type;

    private static TextSpan GetSpan(SourceText text, LspRange range)
    {
        int start = LspPositionConverter.GetOffset(text, range.Start);
        int end = LspPositionConverter.GetOffset(text, range.End);
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                range,
                "The range end precedes its start.");
        }

        return TextSpan.FromBounds(start, end);
    }

    private static Position ToPosition(SourceText text, int offset)
    {
        LinePosition position = text.Lines.GetLinePosition(offset);
        return new Position(position.Line, position.Character);
    }

    private static LspRange ToRange(SourceText text, TextSpan span)
    {
        LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(span);
        return new LspRange(
            new Position(lineSpan.Start.Line, lineSpan.Start.Character),
            new Position(lineSpan.End.Line, lineSpan.End.Character));
    }
}
