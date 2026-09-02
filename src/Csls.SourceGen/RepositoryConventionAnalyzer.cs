using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace Csls.SourceGen;

/// <summary>
/// Enforces the source structure and documentation conventions used by csls.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepositoryConventionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies the one-type-per-file diagnostic.
    /// </summary>
    public const string OneTypePerFileDiagnosticId = "CSLS0001";

    /// <summary>
    /// Identifies the required XML documentation diagnostic.
    /// </summary>
    public const string XmlDocumentationDiagnosticId = "CSLS0002";

    /// <summary>
    /// Identifies the three-line XML summary diagnostic.
    /// </summary>
    public const string ThreeLineSummaryDiagnosticId = "CSLS0003";

    /// <summary>
    /// Identifies the required static-field prefix diagnostic.
    /// </summary>
    public const string StaticFieldPrefixDiagnosticId = "CSLS0004";

    private static readonly DiagnosticDescriptor s_oneTypePerFileRule = new(
        OneTypePerFileDiagnosticId,
        "Put each type in its own file",
        "Type '{0}' must be moved to its own file",
        "Structure",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every class, interface, enum, record, struct, and delegate must have its own file.");

    private static readonly DiagnosticDescriptor s_xmlDocumentationRule = new(
        XmlDocumentationDiagnosticId,
        "Document public and internal APIs",
        "Symbol '{0}' requires triple-slash XML documentation",
        "Documentation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every public or internal type and member must have triple-slash XML documentation.");

    private static readonly DiagnosticDescriptor s_threeLineSummaryRule = new(
        ThreeLineSummaryDiagnosticId,
        "Use three-line XML summaries",
        "Summary for '{0}' must contain an opening tag, one text line, and a closing tag",
        "Documentation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "XML summary elements must use exactly three source lines.");

    private static readonly DiagnosticDescriptor s_staticFieldPrefixRule = new(
        StaticFieldPrefixDiagnosticId,
        "Prefix static fields with s_",
        "Static field '{0}' must start with 's_'",
        "Naming",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Private and internal static fields must use the repository s_ prefix.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            s_oneTypePerFileRule,
            s_xmlDocumentationRule,
            s_threeLineSummaryRule,
            s_staticFieldPrefixRule
        ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeSyntaxTree);
        context.RegisterSymbolAction(
            AnalyzeSymbol,
            SymbolKind.NamedType,
            SymbolKind.Method,
            SymbolKind.Property,
            SymbolKind.Field,
            SymbolKind.Event);
    }

    private static void AnalyzeSyntaxTree(SyntaxTreeAnalysisContext context)
    {
        SyntaxNode root = context.Tree.GetRoot(context.CancellationToken);
        IEnumerable<MemberDeclarationSyntax> declarations = root
            .DescendantNodes()
            .OfType<MemberDeclarationSyntax>()
            .Where(static declaration =>
                declaration is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

        foreach (MemberDeclarationSyntax declaration in declarations.Skip(1))
        {
            string name = declaration switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
                _ => declaration.Kind().ToString()
            };

            context.ReportDiagnostic(Diagnostic.Create(
                s_oneTypePerFileRule,
                declaration.GetLocation(),
                name));
        }
    }

    private static void AnalyzeSymbol(SymbolAnalysisContext context)
    {
        ISymbol symbol = context.Symbol;
        if (symbol.IsImplicitlyDeclared ||
            IsTopLevelStatementsSymbol(symbol, context.CancellationToken))
        {
            return;
        }

        AnalyzeStaticFieldName(symbol, context);
        if (
            !RequiresDocumentation(symbol) ||
            symbol is IMethodSymbol { AssociatedSymbol: not null })
        {
            return;
        }

        string? documentation = symbol.GetDocumentationCommentXml(
            cancellationToken: context.CancellationToken);
        if (string.IsNullOrWhiteSpace(documentation))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_xmlDocumentationRule,
                symbol.Locations.FirstOrDefault(),
                symbol.Name));
            return;
        }

        SyntaxReference? syntaxReference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxReference is null)
        {
            return;
        }

        string source = syntaxReference.GetSyntax(context.CancellationToken).GetLeadingTrivia().ToFullString();
        if (source.Contains("<summary>", StringComparison.Ordinal) &&
            !HasThreeLineSummary(source))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_threeLineSummaryRule,
                symbol.Locations.FirstOrDefault(),
                symbol.Name));
        }
    }

    private static bool HasThreeLineSummary(string source)
    {
        const string OpeningTag = "<summary>";
        const string ClosingTag = "</summary>";
        int openingTagIndex = source.IndexOf(OpeningTag, StringComparison.Ordinal);
        int closingTagIndex = source.IndexOf(
            ClosingTag,
            openingTagIndex + OpeningTag.Length,
            StringComparison.Ordinal);
        if (closingTagIndex < 0)
        {
            return false;
        }

        int firstLineStart = source.LastIndexOf('\n', openingTagIndex) + 1;
        int thirdLineEnd = source.IndexOf('\n', closingTagIndex + ClosingTag.Length);
        if (thirdLineEnd < 0)
        {
            thirdLineEnd = source.Length;
        }

        string[] lines = source
            .Substring(firstLineStart, thirdLineEnd - firstLineStart)
            .Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length != 3)
        {
            return false;
        }

        string openingLine = lines[0].TrimStart(' ', '\t');
        string textLine = lines[1].TrimStart(' ', '\t');
        string closingLine = lines[2].TrimStart(' ', '\t');
        return string.Equals(openingLine, $"/// {OpeningTag}", StringComparison.Ordinal) &&
            textLine.StartsWith("/// ", StringComparison.Ordinal) &&
            textLine.Length > "/// ".Length &&
            string.Equals(closingLine, $"/// {ClosingTag}", StringComparison.Ordinal);
    }

    private static void AnalyzeStaticFieldName(
        ISymbol symbol,
        SymbolAnalysisContext context)
    {
        if (symbol is not IFieldSymbol
            {
                IsStatic: true,
                IsConst: false,
                DeclaredAccessibility: Accessibility.Private or
                    Accessibility.Internal or
                    Accessibility.ProtectedAndInternal
            } field ||
            field.Name.StartsWith("s_", StringComparison.Ordinal))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_staticFieldPrefixRule,
            field.Locations.FirstOrDefault(),
            field.Name));
    }

    private static bool IsTopLevelStatementsSymbol(
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        return symbol.DeclaringSyntaxReferences.Any(
            syntaxReference => syntaxReference.GetSyntax(cancellationToken) is CompilationUnitSyntax);
    }

    private static bool RequiresDocumentation(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { MethodKind: not MethodKind.Ordinary and not MethodKind.Constructor })
        {
            return false;
        }

        return symbol.DeclaredAccessibility is
            Accessibility.Public or
            Accessibility.Internal or
            Accessibility.ProtectedOrInternal or
            Accessibility.ProtectedAndInternal;
    }
}
