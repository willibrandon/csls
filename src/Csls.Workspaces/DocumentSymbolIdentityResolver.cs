using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SymbolKind = Csls.Protocol.SymbolKind;

namespace Csls.Workspaces;

/// <summary>
/// Resolves protocol document-symbol identities from C# declaration syntax.
/// </summary>
internal static class DocumentSymbolIdentityResolver
{
    /// <summary>
    /// Resolves one supported declaration into its display identity.
    /// </summary>
    /// <param name="node">The declaration syntax to inspect.</param>
    /// <returns>The resolved identity, or null when the node is not a document symbol.</returns>
    internal static DocumentSymbolIdentity? Resolve(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node switch
        {
            BaseNamespaceDeclarationSyntax declaration => new(
                declaration.Name.ToString(),
                SymbolKind.Namespace,
                declaration.Name.Span),
            ClassDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText +
                    FormatTypeParameters(declaration.TypeParameterList),
                SymbolKind.Class,
                declaration.Identifier.Span),
            StructDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText +
                    FormatTypeParameters(declaration.TypeParameterList),
                SymbolKind.Struct,
                declaration.Identifier.Span),
            InterfaceDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText +
                    FormatTypeParameters(declaration.TypeParameterList),
                SymbolKind.Interface,
                declaration.Identifier.Span),
            RecordDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText +
                    FormatTypeParameters(declaration.TypeParameterList),
                declaration.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                    ? SymbolKind.Struct
                    : SymbolKind.Class,
                declaration.Identifier.Span),
            EnumDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText,
                SymbolKind.Enum,
                declaration.Identifier.Span),
            DelegateDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText +
                    FormatTypeParameters(declaration.TypeParameterList) +
                    FormatParameters(declaration.ParameterList) +
                    " : " + FormatType(declaration.ReturnType),
                SymbolKind.Method,
                declaration.Identifier.Span),
            MethodDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText +
                    FormatTypeParameters(declaration.TypeParameterList) +
                    FormatParameters(declaration.ParameterList) +
                    " : " + FormatType(declaration.ReturnType),
                SymbolKind.Method,
                declaration.Identifier.Span),
            ConstructorDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText + FormatParameters(declaration.ParameterList),
                SymbolKind.Constructor,
                declaration.Identifier.Span),
            DestructorDeclarationSyntax declaration => new(
                "~" + declaration.Identifier.ValueText +
                    FormatParameters(declaration.ParameterList),
                SymbolKind.Constructor,
                declaration.Identifier.Span),
            OperatorDeclarationSyntax declaration => new(
                "operator " + declaration.OperatorToken.ValueText +
                    FormatParameters(declaration.ParameterList) +
                    " : " + FormatType(declaration.ReturnType),
                SymbolKind.Operator,
                declaration.OperatorToken.Span),
            ConversionOperatorDeclarationSyntax declaration => new(
                (declaration.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword)
                    ? "implicit operator "
                    : "explicit operator ") +
                    FormatType(declaration.Type) +
                    FormatParameters(declaration.ParameterList),
                SymbolKind.Operator,
                declaration.Type.Span),
            PropertyDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText + " : " + FormatType(declaration.Type),
                SymbolKind.Property,
                declaration.Identifier.Span),
            IndexerDeclarationSyntax declaration => new(
                "this" + FormatParameters(
                    declaration.ParameterList.Parameters,
                    "[",
                    "]") + " : " + FormatType(declaration.Type),
                SymbolKind.Property,
                declaration.ThisKeyword.Span),
            EventDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText + " : " + FormatType(declaration.Type),
                SymbolKind.Event,
                declaration.Identifier.Span),
            EnumMemberDeclarationSyntax declaration => new(
                declaration.Identifier.ValueText,
                SymbolKind.EnumMember,
                declaration.Identifier.Span),
            LocalFunctionStatementSyntax declaration => new(
                declaration.Identifier.ValueText +
                    FormatTypeParameters(declaration.TypeParameterList) +
                    FormatParameters(declaration.ParameterList) +
                    " : " + FormatType(declaration.ReturnType),
                SymbolKind.Method,
                declaration.Identifier.Span),
            VariableDeclaratorSyntax declaration when
                declaration.Parent?.Parent is FieldDeclarationSyntax field => new(
                    declaration.Identifier.ValueText +
                        " : " + FormatType(field.Declaration.Type),
                    field.Modifiers.Any(SyntaxKind.ConstKeyword)
                        ? SymbolKind.Constant
                        : SymbolKind.Field,
                    declaration.Identifier.Span),
            VariableDeclaratorSyntax declaration when
                declaration.Parent?.Parent is EventFieldDeclarationSyntax field => new(
                    declaration.Identifier.ValueText +
                        " : " + FormatType(field.Declaration.Type),
                    SymbolKind.Event,
                    declaration.Identifier.Span),
            _ => null
        };
    }

    private static string FormatTypeParameters(TypeParameterListSyntax? parameters) =>
        parameters is null
            ? string.Empty
            : $"<{string.Join(", ", parameters.Parameters.Select(
                static parameter => parameter.Identifier.ValueText))}>";

    private static string FormatParameters(ParameterListSyntax? parameters) =>
        parameters is null
            ? string.Empty
            : FormatParameters(parameters.Parameters, "(", ")");

    private static string FormatParameters(
        SeparatedSyntaxList<ParameterSyntax> parameters,
        string openingDelimiter,
        string closingDelimiter) =>
        openingDelimiter + string.Join(", ", parameters.Select(
            static parameter => FormatType(parameter.Type))) + closingDelimiter;

    private static string FormatType(TypeSyntax? type)
    {
        return type switch
        {
            null => string.Empty,
            ArrayTypeSyntax array => FormatType(array.ElementType) + string.Concat(
                array.RankSpecifiers.Select(static rank =>
                    $"[{new string(',', Math.Max(0, rank.Rank - 1))}]")),
            PointerTypeSyntax pointer => FormatType(pointer.ElementType) + "*",
            NullableTypeSyntax nullable => FormatType(nullable.ElementType) + "?",
            TupleTypeSyntax tuple => $"({string.Join(", ", tuple.Elements.Select(
                static element => FormatTupleElement(element)))})",
            RefTypeSyntax reference => "ref " + FormatType(reference.Type),
            ScopedTypeSyntax scoped => "scoped " + FormatType(scoped.Type),
            PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
            FunctionPointerTypeSyntax functionPointer =>
                $"delegate*<{string.Join(", ", functionPointer.ParameterList.Parameters.Select(
                    static parameter => FormatType(parameter.Type)))}>",
            OmittedTypeArgumentSyntax => string.Empty,
            QualifiedNameSyntax qualified => FormatType(qualified.Right),
            AliasQualifiedNameSyntax aliasQualified => FormatType(aliasQualified.Name),
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText +
                $"<{string.Join(", ", generic.TypeArgumentList.Arguments.Select(
                    static argument => FormatType(argument)))}>",
            _ => type.ToString()
        };
    }

    private static string FormatTupleElement(TupleElementSyntax element) =>
        element.Identifier.RawKind == 0
            ? FormatType(element.Type)
            : $"{FormatType(element.Type)} {element.Identifier.ValueText}";
}
