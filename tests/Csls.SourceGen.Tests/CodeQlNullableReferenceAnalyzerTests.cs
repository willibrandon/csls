using Microsoft.CodeAnalysis;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies explicit captures for null-tested reference locals using real file-backed compiler inputs.
/// </summary>
/// <param name="testContext">The running test's cancellation context.</param>
[TestClass]
public sealed class CodeQlNullableReferenceAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Reports assertion-only proof after earlier null tests for member, indexer, and extension-method access.
    /// </summary>
    /// <param name="probe">The earlier null-sensitive expression.</param>
    /// <param name="access">The subsequent reference access.</param>
    [TestMethod]
    [DataRow("value is null || value.Length == 0", "value.Length")]
    [DataRow("value == null", "(value).Length")]
    [DataRow("null != value", "value[0]")]
    [DataRow("value?.Length == 0", "value.Count()")]
    public async Task ReportsAssertedNullTestedReference(string probe, string access)
    {
        ArgumentNullException.ThrowIfNull(access);
        string source = Source($"bool absent = {probe}; if (condition) return absent ? 1 : 0; " +
            $"Assert.IsNotNull(value); return {access};");

        Diagnostic diagnostic = Assert.ContainsSingle(await CodeQlFileCompilation.AnalyzeAsync(source,
            new CodeQlDereferencedValueMayBeNullAnalyzer(), testContext.CancellationToken).ConfigureAwait(false));

        Assert.AreEqual(CodeQlDereferencedValueMayBeNullAnalyzer.NullableLocalDiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        string expectedAccess = access.EndsWith("()", StringComparison.Ordinal) ? access[..^2] : access;
        Assert.AreEqual(expectedAccess, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Contains("value", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Accepts explicit captures and preserves assertions unrelated to a previously null-tested reference.
    /// </summary>
    /// <param name="body">The method body containing independently proved or unrelated values.</param>
    [TestMethod]
    [DataRow("bool absent = value is null; string captured = value ?? throw new System.InvalidOperationException(); return captured.Length;")]
    [DataRow("bool absent = value is null; string captured = Assert.IsInstanceOfType<string>(value); return captured.Length;")]
    [DataRow("if (value is null) throw new System.InvalidOperationException(); return value.Length;")]
    [DataRow("Assert.IsNotNull(value); return value.Length;")]
    [DataRow("bool absent = other is null; Assert.IsNotNull(value); return value.Length;")]
    [DataRow("bool absent = value is null; Assert.IsNotNull(other); return value?.Length ?? 0;")]
    [DataRow("bool absent = value is null; if (condition) { Assert.IsNotNull(value); return 1; } return value?.Length ?? 0;")]
    [DataRow("System.Func<bool> probe = () => value is null; Assert.IsNotNull(value); return value.Length;")]
    [DataRow("bool absent = value is null; value = input; Assert.IsNotNull(value); return value.Length;")]
    [DataRow("while (value is not null && value.Length > 1) { value = value[1..]; } Assert.IsNotNull(value); return value.Length;")]
    public async Task AcceptsCapturedOrUnrelatedReferences(string body)
    {
        Assert.IsEmpty(await CodeQlFileCompilation.AnalyzeAsync(Source(body),
            new CodeQlDereferencedValueMayBeNullAnalyzer(), testContext.CancellationToken).ConfigureAwait(false));
    }

    private static string Source(string body) => $$"""
        #nullable enable
        using Microsoft.VisualStudio.TestTools.UnitTesting;
        using System.Linq;
        internal static class Reader
        {
            internal static int Read(string? input, string? other, bool condition)
            {
                string? value = input;
                {{body}}
            }
        }
        """;
}
