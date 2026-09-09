using Microsoft.CodeAnalysis;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies null-reference catch detection against exact type identities in real file-backed Roslyn compilations.
/// </summary>
/// <param name="testContext">The running test's cancellation context.</param>
[TestClass]
public sealed class CodeQlNullReferenceCatchAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Rejects direct, qualified, aliased, and filtered catches of the runtime null-reference exception.
    /// </summary>
    /// <param name="handler">The catch clause under analysis.</param>
    [TestMethod]
    [DataRow("catch (NullReferenceException) { throw; }")]
    [DataRow("catch (System.NullReferenceException failure) { Console.WriteLine(failure); }")]
    [DataRow("catch (global::System.NullReferenceException failure) { throw new InvalidOperationException(failure.Message); }")]
    [DataRow("catch (Fault failure) { Console.WriteLine(failure); }")]
    [DataRow("catch (Fault failure) when (failure.Message.Length > 0) { throw; }")]
    public async Task ReportsRuntimeNullReferenceCatch(string handler)
    {
        string source = Source(handler);
        Diagnostic diagnostic = Assert.ContainsSingle(await CodeQlFileCompilation.AnalyzeAsync(source,
            new CodeQlNullReferenceCatchAnalyzer(), testContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(CodeQlNullReferenceCatchAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("catch", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
        Assert.AreEqual(source.IndexOf(handler, StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Preserves ordinary typed handlers and catch clauses for a different type with the same short name.
    /// </summary>
    /// <param name="handler">The distinct or propagating handler.</param>
    [TestMethod]
    [DataRow("catch (System.IO.IOException failure) { Console.WriteLine(failure); }")]
    [DataRow("catch (System.Exception) { throw; }")]
    [DataRow("catch { throw; }")]
    [DataRow("catch (Local.NullReferenceException failure) { Console.WriteLine(failure); }")]
    public async Task AcceptsOtherExceptionTypes(string handler)
    {
        Assert.IsEmpty(await CodeQlFileCompilation.AnalyzeAsync(Source(handler),
            new CodeQlNullReferenceCatchAnalyzer(), testContext.CancellationToken).ConfigureAwait(false));
    }

    private static string Source(string handler) => $$"""
        using System;
        using Fault = System.NullReferenceException;
        namespace Local { public sealed class NullReferenceException : Exception { } }
        internal static class Probe
        {
            internal static void Invoke(Action operation)
            {
                try { operation(); }
                {{handler}}
            }
        }
        """;
}
