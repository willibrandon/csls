using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies guarded local projections make later conditional access redundant only while unchanged.
/// </summary>
/// <param name="testContext">The running test's cancellation context.</param>
[TestClass]
public sealed class CodeQlGuardedConditionalAccessAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Reports direct guards and the chained projection pattern from async caller symbol discovery.
    /// </summary>
    /// <param name="setup">The guard and preceding declarations.</param>
    [TestMethod]
    [DataRow("if (value is null) return 0;")]
    [DataRow("if (value?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); if (projected is null) return 0;")]
    [DataRow("string? projected = value?.Trim(); if (projected is not { }) return 0;")]
    [DataRow("string? projected = value?.Trim(); string? alias = projected; if (alias is null) return 0;")]
    public async Task ReportsNonNullReceiverAfterGuardedProjection(string setup)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(setup).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlConstantConditionAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("Null test is always 'false' after the earlier condition",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        Assert.AreEqual("value?.Length", (await diagnostic.Location.SourceTree.GetTextAsync(testContext.CancellationToken)
            .ConfigureAwait(false)).ToString(diagnostic.Location.SourceSpan));
    }

    /// <summary>
    /// Keeps conditional access when mutation, references, or incomplete guards invalidate the proof.
    /// </summary>
    /// <param name="setup">The declarations and operations preceding the nullable receiver use.</param>
    [TestMethod]
    [DataRow("string? projected = value?.Trim(); value = null; if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); projected = string.Empty; if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); if (projected?.Length is not int length) return 0; value = null;")]
    [DataRow("string? projected = value?.Trim(); Change(ref value); if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); Change(ref projected); if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); System.Action change = () => value = null; change(); if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); ref string? alias = ref value; alias = null; if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim() ?? string.Empty; if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = other?.Trim(); if (projected?.Length is not int length) return 0;")]
    [DataRow("string? projected = value?.Trim(); if (projected?.Length is not int length) System.Console.WriteLine(projected);")]
    [DataRow("string? projected = value?.Trim(); if (projected is not null) return 0;")]
    [DataRow("string? projected = value?.Trim(); if (projected?.Length is not int length) { if (other is null) return 0; }")]
    public async Task AcceptsUnprovenOrInvalidatedReceiver(string setup)
    {
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(setup).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Keeps mutable property receivers and ref parameters independent of earlier projections.
    /// </summary>
    /// <param name="parameter">The actual receiver parameter declaration.</param>
    /// <param name="receiver">The mutable receiver expression.</param>
    [TestMethod]
    [DataRow("ref string? value", "value")]
    [DataRow("string? value", "Current")]
    public async Task AcceptsMutableReceivers(string parameter, string receiver)
    {
        string source = $$"""
            internal static class Reader
            {
                private static string? Current => System.Console.ReadLine();
                internal static int Read({{parameter}})
                {
                    string? projected = {{receiver}}?.Trim();
                    if (projected?.Length is not int length) return 0;
                    return {{receiver}}?.Length ?? -1;
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await CodeQlFileCompilation.AnalyzeAsync(source,
            new CodeQlConstantConditionAnalyzer(), testContext.CancellationToken).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    /// <summary>
    /// Preserves nullable receivers when a user conversion can materialize a projection from null.
    /// </summary>
    [TestMethod]
    public async Task AcceptsUserConversionThatMaterializesNullProjection()
    {
        const string Source = """
            internal sealed class Projection
            {
                public static implicit operator Projection(string? value) => new();
                internal static int Read(string? value)
                {
                    Projection? projected = value?.Trim();
                    if (projected is null) return 0;
                    return value?.Length ?? -1;
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await CodeQlFileCompilation.AnalyzeAsync(Source,
            new CodeQlConstantConditionAnalyzer(), testContext.CancellationToken).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string setup) => CodeQlFileCompilation.AnalyzeAsync(
        $$"""
        internal static class Reader
        {
            internal static int Read(string? value, string? other)
            {
                try
                {
                    {{setup}}
                    return value?.Length ?? -1;
                }
                finally { System.Console.WriteLine("read"); }
            }
            private static void Change(ref string? value) => value = null;
        }
        """, new CodeQlConstantConditionAnalyzer(), testContext.CancellationToken);
}
