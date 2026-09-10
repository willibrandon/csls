using Microsoft.CodeAnalysis;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies typed capture of asserted nullable locals using file-backed compiler inputs.
/// </summary>
[TestClass]
public sealed class CodeQlNullableLocalAnalyzerTests
{
    /// <summary>
    /// Gets the framework cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Requires a typed capture when an asserted nullable local is unwrapped after synchronous or suspended work.
    /// </summary>
    /// <param name="expression">The nullable local access.</param>
    /// <param name="suspend">Whether execution suspends between the assertion and access.</param>
    [TestMethod]
    [DataRow("value.Value", false)]
    [DataRow("value.Value", true)]
    [DataRow("(value).Value", true)]
    [DataRow("value!.Value", true)]
    public async Task ReportsAssertedNullableLocalUnwrapping(string expression, bool suspend)
    {
        string resultType = suspend ? "async Task<int>" : "int";
        string suspension = suspend ? "await Task.Yield();" : string.Empty;
        string source = $$"""
            #nullable enable
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            using System.Threading.Tasks;
            internal static class Reader
            {
                internal static {{resultType}} Read(int? input)
                {
                    int? value = input;
                    Assert.IsNotNull(value);
                    {{suspension}}
                    return {{expression}};
                }
            }
            """;

        Diagnostic diagnostic = Assert.ContainsSingle(await CodeQlFileCompilation.AnalyzeAsync(
            source, new CodeQlDereferencedValueMayBeNullAnalyzer(), TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(CodeQlDereferencedValueMayBeNullAnalyzer.NullableLocalDiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(expression, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.Contains("value", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Keeps typed captures, explicit local guards, and assertions for other values or branches accepted.
    /// </summary>
    /// <param name="body">The method body with independently proved or unrelated values.</param>
    [TestMethod]
    [DataRow("int value = Assert.IsInstanceOfType<int>(input); await Task.Yield(); return value;")]
    [DataRow("int value = input ?? throw new System.InvalidOperationException(); await Task.Yield(); return value;")]
    [DataRow("int? value = input; if (value.HasValue) return value.Value; return -1;")]
    [DataRow("int? value = input; int? other = 1; Assert.IsNotNull(other); return value.Value;")]
    [DataRow("int? value = input; int result = value.Value; Assert.IsNotNull(value); return result;")]
    [DataRow("int? value = input; if (condition) { Assert.IsNotNull(value); return 1; } return value.Value;")]
    public async Task AcceptsTypedCapturesAndIndependentAssertions(string body)
    {
        string source = $$"""
            #nullable enable
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            using System.Threading.Tasks;
            internal static class Reader
            {
                internal static async Task<int> Read(int? input, bool condition)
                {
                    await Task.Yield();
                    {{body}}
                }
            }
            """;

        Assert.IsEmpty(await CodeQlFileCompilation.AnalyzeAsync(source,
            new CodeQlDereferencedValueMayBeNullAnalyzer(), TestContext.CancellationToken).ConfigureAwait(false));
    }
}
