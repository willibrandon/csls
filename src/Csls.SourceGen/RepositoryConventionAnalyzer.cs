using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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

    private static readonly Regex s_threeLineSummaryPattern = new(
        @"(?m)^[ \t]*/// <summary>\r?\n[ \t]*/// [^\r\n]+\r?\n[ \t]*/// </summary>$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [s_oneTypePerFileRule, s_xmlDocumentationRule, s_threeLineSummaryRule];

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
            IsTopLevelStatementsSymbol(symbol, context.CancellationToken) ||
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
            !s_threeLineSummaryPattern.IsMatch(source))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_threeLineSummaryRule,
                symbol.Locations.FirstOrDefault(),
                symbol.Name));
        }
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
