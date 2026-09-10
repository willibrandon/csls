using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies missed-Select diagnostics at CodeQL's inclusive source-column boundary.
/// </summary>
/// <param name="testContext">The active test cancellation context.</param>
[TestClass]
public sealed class CodeQlMissedSelectAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Applies the statement-width boundary to checked metadata-token projections.
    /// </summary>
    /// <param name="variableName">The mapped variable name that determines declaration width.</param>
    /// <param name="declarationLength">The expected exclusive Roslyn span length.</param>
    /// <param name="expectedDiagnostic">Whether the declaration fits CodeQL's projection limit.</param>
    [TestMethod]
    [DataRow("toke", 65, true)]
    [DataRow("token", 66, true)]
    [DataRow("tokens", 67, false)]
    public async Task ChecksInclusiveDeclarationWidth(string variableName, int declarationLength, bool expectedDiagnostic)
    {
        string declaration = $"uint {variableName} = checked((uint)MetadataTokens.GetToken(methodHandle));";
        string source = $$"""
            using System;
            using System.Reflection.Metadata;
            using System.Reflection.Metadata.Ecma335;

            internal static class Reader
            {
                internal static void Read(TypeDefinition type)
                {
                    foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
                    {
                        {{declaration}}
                        Console.WriteLine({{variableName}});
                    }
                }
            }
            """;
        Assert.AreEqual(declarationLength, declaration.Length);
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        if (expectedDiagnostic)
        {
            Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
            Assert.AreEqual(CodeQlMissedSelectAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.AreEqual(source.IndexOf(declaration, StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
            Assert.AreEqual(declarationLength, diagnostic.Location.SourceSpan.Length);
            Assert.AreEqual("Loop variable 'methodHandle' is immediately mapped; project it with Select before iteration",
                diagnostic.GetMessage(CultureInfo.InvariantCulture));
        }
        else
        {
            Assert.IsEmpty(diagnostics);
        }
    }

    /// <summary>
    /// Accepts the explicit checked token projection used by the async-consumer tracker.
    /// </summary>
    [TestMethod]
    public async Task AcceptsExplicitCheckedTokenProjection()
    {
        const string Source = """
            using System;
            using System.Linq;
            using System.Reflection.Metadata;
            using System.Reflection.Metadata.Ecma335;

            internal static class Reader
            {
                internal static void Read(TypeDefinition type)
                {
                    foreach (uint token in type.GetMethods().Select(static handle => checked((uint)MetadataTokens.GetToken(handle))))
                    {
                        Console.WriteLine(token);
                    }
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source).ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) => CodeQlFileCompilation.AnalyzeAsync(
        source, new CodeQlMissedSelectAnalyzer(), testContext.CancellationToken);
}
