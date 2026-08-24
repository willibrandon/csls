using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using System.Globalization;
using LspCodeAction = Csls.Protocol.CodeAction;
using LspDiagnostic = Csls.Protocol.Diagnostic;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Produces verified implementations for required members of one selected interface.
/// </summary>
internal static class WorkspaceImplementInterfaceCodeActionService
{
    private const string MissingInterfaceMemberDiagnosticId = "CS0535";
    private const string QuickFixCodeActionKind = "quickfix";

    /// <summary>
    /// Gets a concrete implementation action for the interface at the requested source range.
    /// </summary>
    /// <param name="document">The current Roslyn document.</param>
    /// <param name="parameters">The target range and editor diagnostic context.</param>
    /// <param name="createWorkspaceEditAsync">Creates a version-aware LSP workspace edit.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The verified action, or an empty list when no implementation is required.</returns>
    internal static async Task<IReadOnlyList<LspCodeAction>> GetActionsAsync(
        Document document,
        CodeActionParams parameters,
        Func<Solution, Solution, CancellationToken, Task<WorkspaceEdit>>
            createWorkspaceEditAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(createWorkspaceEditAsync);

        SourceText sourceText = await document.GetTextAsync(cancellationToken)
            .ConfigureAwait(false);
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The implement-interface document has no syntax root.");
        SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The implement-interface document has no semantic model.");
        BaseTypeSyntax? baseType = FindBaseType(root, sourceText, parameters.Range);
        TypeDeclarationSyntax? typeDeclaration = baseType?.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        INamedTypeSymbol? interfaceType = baseType is null
            ? null
            : semanticModel.GetTypeInfo(baseType.Type, cancellationToken).Type as INamedTypeSymbol;
        INamedTypeSymbol? containingType = typeDeclaration is null
            ? null
            : semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken);
        if (baseType is null ||
            typeDeclaration is null ||
            interfaceType is not { TypeKind: TypeKind.Interface } ||
            containingType is null)
        {
            return [];
        }

        List<ISymbol> missingMembers = GetMissingMembers(
            containingType,
            interfaceType,
            cancellationToken);
        if (missingMembers.Count == 0)
        {
            return [];
        }

        Document? changedDocument = await TryImplementAsync(
            document,
            semanticModel,
            root,
            typeDeclaration,
            baseType,
            interfaceType,
            missingMembers,
            cancellationToken).ConfigureAwait(false);
        if (changedDocument is null)
        {
            return [];
        }

        WorkspaceEdit edit = await createWorkspaceEditAsync(
            document.Project.Solution,
            changedDocument.Project.Solution,
            cancellationToken).ConfigureAwait(false);
        if (edit.DocumentChanges.Count == 0)
        {
            return [];
        }

        IReadOnlyList<LspDiagnostic>? diagnostics = GetClientDiagnostics(
            parameters.Context.Diagnostics,
            sourceText,
            typeDeclaration.Span);
        return
        [
            new LspCodeAction
            {
                Title = "Implement interface",
                Kind = QuickFixCodeActionKind,
                Diagnostics = diagnostics,
                IsPreferred = true,
                Edit = edit
            }
        ];
    }

    private static BaseTypeSyntax? FindBaseType(
        SyntaxNode root,
        SourceText sourceText,
        LspRange range)
    {
        int start = LspPositionConverter.GetOffset(sourceText, range.Start);
        int end = LspPositionConverter.GetOffset(sourceText, range.End);
        var requestedSpan = TextSpan.FromBounds(start, Math.Max(start, end));
        int tokenOffset = Math.Clamp(
            start == root.FullSpan.End ? start - 1 : start,
            0,
            Math.Max(0, root.FullSpan.End - 1));
        BaseTypeSyntax? ancestor = root
            .FindToken(tokenOffset, findInsideTrivia: true)
            .Parent?
            .AncestorsAndSelf()
            .OfType<BaseTypeSyntax>()
            .FirstOrDefault();
        if (ancestor is not null && Intersects(ancestor.Type.Span, requestedSpan, start))
        {
            return ancestor;
        }

        return root
            .DescendantNodes(requestedSpan)
            .OfType<BaseTypeSyntax>()
            .Where(baseType => Intersects(baseType.Type.Span, requestedSpan, start))
            .OrderBy(static baseType => baseType.Span.Length)
            .FirstOrDefault();
    }

    private static bool Intersects(TextSpan candidate, TextSpan requested, int position) =>
        requested.IsEmpty
            ? candidate.Contains(position)
            : candidate.IntersectsWith(requested);

    private static List<ISymbol> GetMissingMembers(
        INamedTypeSymbol containingType,
        INamedTypeSymbol selectedInterface,
        CancellationToken cancellationToken)
    {
        var interfaces = new List<INamedTypeSymbol>();
        var visitedInterfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        AddInterfaceAndBases(selectedInterface, interfaces, visitedInterfaces, cancellationToken);

        var missingMembers = new List<ISymbol>();
        foreach (INamedTypeSymbol interfaceType in interfaces)
        {
            foreach (ISymbol member in interfaceType.GetMembers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!RequiresImplementation(member) ||
                    containingType.FindImplementationForInterfaceMember(member) is not null)
                {
                    continue;
                }

                missingMembers.Add(member);
            }
        }

        return missingMembers;
    }

    private static void AddInterfaceAndBases(
        INamedTypeSymbol interfaceType,
        List<INamedTypeSymbol> interfaces,
        HashSet<INamedTypeSymbol> visitedInterfaces,
        CancellationToken cancellationToken)
    {
        if (!visitedInterfaces.Add(interfaceType))
        {
            return;
        }

        foreach (INamedTypeSymbol baseInterface in interfaceType.Interfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddInterfaceAndBases(
                baseInterface,
                interfaces,
                visitedInterfaces,
                cancellationToken);
        }

        interfaces.Add(interfaceType);
    }

    private static bool RequiresImplementation(ISymbol member) => member switch
    {
        IMethodSymbol
        {
            IsAbstract: true,
            MethodKind: MethodKind.Ordinary or
                MethodKind.UserDefinedOperator or
                MethodKind.Conversion
        } => true,
        IPropertySymbol { IsAbstract: true } => true,
        IEventSymbol { IsAbstract: true } => true,
        _ => false
    };

    private static async Task<Document?> TryImplementAsync(
        Document document,
        SemanticModel originalSemanticModel,
        SyntaxNode root,
        TypeDeclarationSyntax typeDeclaration,
        BaseTypeSyntax baseType,
        INamedTypeSymbol interfaceType,
        IReadOnlyList<ISymbol> missingMembers,
        CancellationToken cancellationToken)
    {
        Compilation compilation = originalSemanticModel.Compilation;
        INamedTypeSymbol exceptionType = compilation.GetTypeByMetadataName(
                "System.NotImplementedException")
            ?? throw new InvalidOperationException(
                "The target compilation has no System.NotImplementedException type.");
        var generator = SyntaxGenerator.GetGenerator(document);
        SyntaxNode throwStatement = generator.ThrowStatement(
            generator.ObjectCreationExpression(generator.TypeExpression(exceptionType)));
        SyntaxNode[] generatedMembers =
        [
            .. missingMembers.Select(member => CreateMember(
                generator,
                member,
                throwStatement))
        ];

        var typeAnnotation = new SyntaxAnnotation();
        var interfaceAnnotation = new SyntaxAnnotation();
        var generatedAnnotation = new SyntaxAnnotation();
        BaseTypeSyntax annotatedBaseType = baseType.WithType(
            baseType.Type.WithAdditionalAnnotations(interfaceAnnotation));
        TypeDeclarationSyntax annotatedType = typeDeclaration
            .ReplaceNode(baseType, annotatedBaseType)
            .WithAdditionalAnnotations(typeAnnotation);
        generatedMembers =
        [
            .. generatedMembers.Select(member => member.WithAdditionalAnnotations(
                generatedAnnotation,
                Simplifier.Annotation,
                Simplifier.AddImportsAnnotation,
                Formatter.Annotation))
        ];
        SyntaxNode changedType = generator.AddMembers(annotatedType, generatedMembers);
        Document changedDocument = document.WithSyntaxRoot(
            root.ReplaceNode(typeDeclaration, changedType));
        changedDocument = await ImportAdder.AddImportsAsync(
            changedDocument,
            generatedAnnotation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        changedDocument = await Simplifier.ReduceAsync(
            changedDocument,
            generatedAnnotation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        changedDocument = await Formatter.FormatAsync(
            changedDocument,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await IsValidImplementationAsync(
                originalSemanticModel,
                interfaceType,
                changedDocument,
                typeAnnotation,
                interfaceAnnotation,
                cancellationToken)
            .ConfigureAwait(false)
            ? changedDocument
            : null;
    }

    private static SyntaxNode CreateMember(
        SyntaxGenerator generator,
        ISymbol member,
        SyntaxNode throwStatement)
    {
        SyntaxNode declaration = member switch
        {
            IMethodSymbol
            {
                MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion
            } method => generator.OperatorDeclaration(method, [throwStatement]),
            IMethodSymbol method => generator.MethodDeclaration(method, [throwStatement]),
            IPropertySymbol { IsIndexer: true } property => generator.IndexerDeclaration(
                property,
                property.GetMethod is null ? null : [throwStatement],
                property.SetMethod is null ? null : [throwStatement]),
            IPropertySymbol property => generator.PropertyDeclaration(
                property,
                property.GetMethod is null ? null : [throwStatement],
                property.SetMethod is null ? null : [throwStatement]),
            IEventSymbol @event => generator.CustomEventDeclaration(
                @event,
                [throwStatement],
                [throwStatement]),
            _ => throw new InvalidOperationException(
                $"Unsupported interface member kind {member.Kind}.")
        };
        DeclarationModifiers modifiers = DeclarationModifiers.From(member)
            .WithIsAbstract(false)
            .WithIsVirtual(false)
            .WithIsOverride(false)
            .WithIsSealed(false)
            .WithIsReadOnly(false)
            .WithIsExtern(false);
        declaration = generator.WithModifiers(declaration, modifiers);
        declaration = generator.WithAccessibility(declaration, Accessibility.Public);
        return AddThrowBodies(declaration, (StatementSyntax)throwStatement);
    }

    private static SyntaxNode AddThrowBodies(
        SyntaxNode declaration,
        StatementSyntax throwStatement)
    {
        BlockSyntax body = SyntaxFactory.Block(throwStatement);
        return declaration switch
        {
            MethodDeclarationSyntax method => method
                .WithBody(body)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            OperatorDeclarationSyntax @operator => @operator
                .WithBody(body)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            ConversionOperatorDeclarationSyntax conversion => conversion
                .WithBody(body)
                .WithExpressionBody(null)
                .WithSemicolonToken(default),
            EventDeclarationSyntax @event => @event.WithAccessorList(
                AddThrowBodies(@event.AccessorList, body)),
            BasePropertyDeclarationSyntax property => property.WithAccessorList(
                AddThrowBodies(property.AccessorList, body)),
            _ => throw new InvalidOperationException(
                $"Unsupported generated declaration {declaration.Kind()}.")
        };
    }

    private static AccessorListSyntax AddThrowBodies(
        AccessorListSyntax? accessorList,
        BlockSyntax body)
    {
        if (accessorList is null)
        {
            throw new InvalidOperationException(
                "The generated interface member has no accessor list.");
        }

        return accessorList.WithAccessors(
            SyntaxFactory.List(accessorList.Accessors.Select(accessor => accessor
                .WithBody(body)
                .WithExpressionBody(null)
                .WithSemicolonToken(default))));
    }

    private static async Task<bool> IsValidImplementationAsync(
        SemanticModel originalSemanticModel,
        INamedTypeSymbol originalInterface,
        Document changedDocument,
        SyntaxAnnotation typeAnnotation,
        SyntaxAnnotation interfaceAnnotation,
        CancellationToken cancellationToken)
    {
        SyntaxNode changedRoot = await changedDocument.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The changed implement-interface document has no syntax root.");
        TypeDeclarationSyntax? changedType = changedRoot
            .GetAnnotatedNodes(typeAnnotation)
            .OfType<TypeDeclarationSyntax>()
            .SingleOrDefault();
        TypeSyntax? changedInterfaceSyntax = changedRoot
            .GetAnnotatedNodes(interfaceAnnotation)
            .OfType<TypeSyntax>()
            .SingleOrDefault();
        if (changedType is null || changedInterfaceSyntax is null)
        {
            return false;
        }

        SemanticModel changedSemanticModel = await changedDocument
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The changed implement-interface document has no semantic model.");
        INamedTypeSymbol? changedContainingType = changedSemanticModel.GetDeclaredSymbol(
            changedType,
            cancellationToken);
        var changedInterface = changedSemanticModel.GetTypeInfo(
            changedInterfaceSyntax,
            cancellationToken).Type as INamedTypeSymbol;
        if (changedContainingType is null ||
            changedInterface is null ||
            !string.Equals(
                changedInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                originalInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                StringComparison.Ordinal) ||
            GetMissingMembers(
                changedContainingType,
                changedInterface,
                cancellationToken).Count != 0)
        {
            return false;
        }

        HashSet<(string Id, string Message)> originalErrors =
        [
            .. originalSemanticModel
                .GetDiagnostics(cancellationToken: cancellationToken)
                .Where(static diagnostic =>
                    diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(static diagnostic => (
                    diagnostic.Id,
                    diagnostic.GetMessage(CultureInfo.InvariantCulture)))
        ];
        return !changedSemanticModel
            .GetDiagnostics(cancellationToken: cancellationToken)
            .Where(static diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(static diagnostic => (
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture)))
            .Any(diagnostic => !originalErrors.Contains(diagnostic));
    }

    private static LspDiagnostic[]? GetClientDiagnostics(
        IReadOnlyList<LspDiagnostic> diagnostics,
        SourceText sourceText,
        TextSpan typeSpan)
    {
        LspDiagnostic[] matchingDiagnostics =
        [
            .. diagnostics.Where(diagnostic =>
                string.Equals(
                    diagnostic.Code,
                    MissingInterfaceMemberDiagnosticId,
                    StringComparison.Ordinal) &&
                ToTextSpan(sourceText, diagnostic.Range).IntersectsWith(typeSpan))
        ];
        return matchingDiagnostics.Length == 0 ? null : matchingDiagnostics;
    }

    private static TextSpan ToTextSpan(SourceText sourceText, LspRange range)
    {
        int start = LspPositionConverter.GetOffset(sourceText, range.Start);
        int end = LspPositionConverter.GetOffset(sourceText, range.End);
        return TextSpan.FromBounds(start, Math.Max(start, end));
    }
}
