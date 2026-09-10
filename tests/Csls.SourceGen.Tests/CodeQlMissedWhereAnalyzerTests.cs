using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies the local guard for CodeQL missed-Where findings.
/// </summary>
/// <param name="testContext">The test cancellation context.</param>
[TestClass]
public sealed class CodeQlMissedWhereAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Verifies a loop that conditionally returns from its only branch is rejected.
    /// </summary>
    [TestMethod]
    public async Task ReportsConditionalReturnFilter()
    {
        const string Source = """
            using System.Collections.Generic;

            internal static class Projection
            {
                internal static bool ContainsPositive(IEnumerable<int> values)
                {
                    foreach (int value in values)
                    {
                        if (value > 0)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlMissedWhereAnalyzer.DiagnosticId, diagnostic.Id);
    }

    /// <summary>
    /// Verifies explicit sequence filtering remains accepted.
    /// </summary>
    [TestMethod]
    public async Task AcceptsExplicitWhereFilter()
    {
        const string Source = """
            using System.Collections.Generic;
            using System.Linq;

            internal static class Projection
            {
                internal static bool ContainsPositive(IEnumerable<int> values)
                {
                    foreach (int value in values.Where(static value => value > 0))
                    {
                        return true;
                    }

                    return false;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(Source)
            .ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Checks conditional member filtering after OfType against the equivalent explicit query.
    /// </summary>
    /// <param name="explicitFilter">Whether the sequence carries its own filter.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChecksMemberFilteringAfterOfType(bool explicitFilter)
    {
        const string Predicate = "!inherited.IsStatic && inherited.DeclaredAccessibility != Accessibility.Private && " +
            "!accesses.ContainsKey(inherited.OriginalDefinition)";
        string filter = explicitFilter ? $".Where(inherited => {Predicate})" : string.Empty;
        string body = explicitFilter ? "return true;" : $"if ({Predicate}) {{ return true; }}";
        string source = $$"""
            using System.Collections.Concurrent;
            using System.Linq;
            using Microsoft.CodeAnalysis;

            internal static class MemberInspection
            {
                internal static bool Inspect(IFieldSymbol field, ConcurrentDictionary<ISymbol, byte> accesses)
                {
                    for (INamedTypeSymbol parent = field.ContainingType.BaseType; parent != null; parent = parent.BaseType)
                    {
                        foreach (IFieldSymbol inherited in parent.GetMembers(field.Name).OfType<IFieldSymbol>(){{filter}})
                        {
                            {{body}}
                        }
                    }
                    return false;
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        if (explicitFilter)
        {
            Assert.IsEmpty(diagnostics);
        }
        else
        {
            Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
            Assert.AreEqual(CodeQlMissedWhereAnalyzer.DiagnosticId, diagnostic.Id);
            Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.AreEqual(source.IndexOf("foreach", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
            Assert.AreEqual("Loop variable 'inherited' implicitly filters its sequence; express the filter before iteration",
                diagnostic.GetMessage(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Checks the shipping field-masking analyzer for implicit sequence filters before remote analysis.
    /// </summary>
    [TestMethod]
    public async Task ChecksProductionFieldMaskingAnalyzer()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "Csls.slnx")))
        {
            repository = repository.Parent;
        }

        Assert.IsNotNull(repository, "The analyzer regression requires the repository checkout.");
        string path = Path.Join(repository.FullName, "src", "Csls.SourceGen", "CodeQlFieldMasksBaseFieldAnalyzer.cs");
        string source = await File.ReadAllTextAsync(path, testContext.CancellationToken).ConfigureAwait(false);
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        Assert.IsEmpty(diagnostics);
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) => CodeQlFileCompilation.AnalyzeAsync(
        source, new CodeQlMissedWhereAnalyzer(), testContext.CancellationToken);
}
