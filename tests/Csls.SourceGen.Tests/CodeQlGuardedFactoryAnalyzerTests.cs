using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies null correlations across real-file static factory declarations and their callers.
/// </summary>
/// <param name="testContext">The active test's cancellation context.</param>
[TestClass]
public sealed class CodeQlGuardedFactoryAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Rejects null tests whose value follows from the factory's guard and reference construction.
    /// </summary>
    /// <param name="condition">The caller's short-circuit null condition.</param>
    /// <param name="expression">The exact redundant expression to diagnose.</param>
    /// <param name="value">The proven Boolean result.</param>
    [TestMethod]
    [DataRow("value is not null && output is not null", "output is not null", "true")]
    [DataRow("value is not null && output is null", "output is null", "false")]
    [DataRow("value is null && output is null", "output is null", "true")]
    [DataRow("value is null && output is not null", "output is not null", "false")]
    [DataRow("value is null || output is not null", "output is not null", "true")]
    [DataRow("value is not null || output is not null", "output is not null", "false")]
    public async Task ReportsNullTestsCorrelatedWithGuardedFactory(string condition, string expression, string value)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(condition, string.Empty,
            "return new System.IO.MemoryStream();").ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlConstantConditionAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains($"'{value}'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        Assert.AreEqual(expression, (await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken)
            .ConfigureAwait(false)).ToString(diagnostic.Location.SourceSpan));
    }

    /// <summary>
    /// Preserves a null test after mutation, aliasing, or passing a different factory argument.
    /// </summary>
    /// <param name="mutation">The caller operation that invalidates the earlier correlation.</param>
    [TestMethod]
    [DataRow("value = null;")]
    [DataRow("output = null;")]
    [DataRow("Change(ref value);")]
    [DataRow("Change(ref output);")]
    [DataRow("System.Action change = () => value = null; change();")]
    [DataRow("System.Action change = () => output = null; change();")]
    [DataRow("ref string? alias = ref value; alias = null;")]
    public async Task AcceptsFactoryCorrelationAfterMutation(string mutation)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("value is not null && output is not null",
            mutation, "return new System.IO.MemoryStream();", useUsing: false).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Keeps null tests when the factory can return an unknown value or an additional null branch.
    /// </summary>
    /// <param name="body">The unproven factory return paths.</param>
    [TestMethod]
    [DataRow("return null;")]
    [DataRow("return Other();")]
    [DataRow("if (input.Length == 0) return null; return new System.IO.MemoryStream();")]
    [DataRow("try { return new System.IO.MemoryStream(); } catch (System.IO.IOException) { return null; }")]
    public async Task AcceptsUnprovenFactoryReturn(string body)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync("value is not null && output is not null",
            string.Empty, body).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Keeps independent argument guards and conversions that can change a factory result's nullness.
    /// </summary>
    /// <param name="argument">The expression supplied to the factory.</param>
    /// <param name="storageType">The caller's local storage type.</param>
    [TestMethod]
    [DataRow("other", "System.IO.MemoryStream?")]
    [DataRow("new string(value ?? string.Empty)", "System.IO.MemoryStream?")]
    [DataRow("value", "Converted?")]
    public async Task AcceptsUncorrelatedFactoryArgumentOrConversion(string argument, string storageType)
    {
        string source = $$"""
            internal sealed class Converted
            {
                public static implicit operator Converted?(System.IO.MemoryStream? input) => null;
                internal static bool Read(string? value, string? other)
                {
                    {{storageType}} output = Create({{argument}});
                    return value is not null && output is not null;
                }
                private static System.IO.MemoryStream? Create(string? input)
                {
                    if (input is null) return null;
                    return new System.IO.MemoryStream();
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await CodeQlFileCompilation.AnalyzeAsync(source,
            new CodeQlConstantConditionAnalyzer(), testContext.CancellationToken).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string condition, string mutation, string factoryBody,
        bool useUsing = true) => CodeQlFileCompilation.AnalyzeAsync(
        $$"""
        internal static class Reader
        {
            internal static async System.Threading.Tasks.Task<bool> Read(string? value)
            {
                {{(useUsing ? "using " : string.Empty)}}System.IO.MemoryStream? output = Create(value);
                {{mutation}}
                await System.Threading.Tasks.Task.Yield();
                return {{condition}};
            }
            private static System.IO.MemoryStream? Create(string? input)
            {
                if (input is null)
                {
                    return null;
                }
                string text = input.Trim();
                System.Console.WriteLine(text);
                {{factoryBody}}
            }
            private static System.IO.MemoryStream? Other() => null;
            private static void Change<T>(ref T? input) where T : class => input = null;
        }
        """, new CodeQlConstantConditionAnalyzer(), testContext.CancellationToken);
}
