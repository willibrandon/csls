using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies native imports are subject to the repository's by-reference parameter limit.
/// </summary>
/// <param name="testContext">The running test's cooperative cancellation context.</param>
[TestClass]
public sealed class CodeQlTooManyRefParametersAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Reports an oversized LibraryImport declaration even with a partial implementation.
    /// </summary>
    [TestMethod]
    public async Task ReportsNativeImportWithFourByReferenceParameters()
    {
        const string Source = """
            using System.Runtime.InteropServices;
            internal static partial class Native
            {
                [LibraryImport("libc")]
                private static partial int Query(ref ulong address, out ulong size, ref uint depth, ref uint count);

                private static partial int Query(ref ulong address, out ulong size, ref uint depth, ref uint count)
                {
                    size = address;
                    return 0;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await CodeQlFileCompilation.AnalyzeAsync(Source,
            new CodeQlTooManyRefParametersAnalyzer(), testContext.CancellationToken).ConfigureAwait(false);

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlTooManyRefParametersAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.Contains("4", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Keeps bounded native imports available for source-generated platform calls.
    /// </summary>
    [TestMethod]
    public async Task AcceptsNativeImportWithTwoByReferenceParameters()
    {
        const string Source = """
            using System.Runtime.InteropServices;
            internal static partial class Native
            {
                [LibraryImport("libc")]
                private static partial int Query(ref ulong address, ref uint depth, nint buffer);

                private static partial int Query(ref ulong address, ref uint depth, nint buffer)
                {
                    return 0;
                }
            }
            """;

        ImmutableArray<Diagnostic> diagnostics = await CodeQlFileCompilation.AnalyzeAsync(Source,
            new CodeQlTooManyRefParametersAnalyzer(), testContext.CancellationToken).ConfigureAwait(false);

        Assert.IsEmpty(diagnostics);
    }
}
