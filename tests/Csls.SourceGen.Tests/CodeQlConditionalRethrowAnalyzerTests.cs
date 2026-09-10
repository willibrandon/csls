using Microsoft.CodeAnalysis;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies explicit captured-exception guards before returning a successfully read value.
/// </summary>
[TestClass]
public sealed class CodeQlConditionalRethrowAnalyzerTests
{
    /// <summary>
    /// Gets the framework cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects conditional rethrows that obscure the successful path through an exception-check helper.
    /// </summary>
    /// <param name="expression">The conditional captured-exception rethrow.</param>
    [TestMethod]
    [DataRow("failure?.Throw()")]
    [DataRow("(failure)?.Throw()")]
    public async Task ReportsConditionalCapturedExceptionRethrow(string expression)
    {
        string source = CreateSource($"{expression};");
        Diagnostic diagnostic = Assert.ContainsSingle(await CodeQlFileCompilation.AnalyzeAsync(source,
            new CodeQlUselessAssignmentToLocalAnalyzer(), TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(CodeQlUselessAssignmentToLocalAnalyzer.ConditionalRethrowDiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(expression, source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
    }

    /// <summary>
    /// Preserves explicit exception guards and ordinary nullable observations of the captured exception.
    /// </summary>
    /// <param name="body">The helper with an explicit successful return path.</param>
    [TestMethod]
    [DataRow("if (failure is { } captured) { captured.Throw(); }")]
    [DataRow("if (failure is null) return; failure.Throw();")]
    [DataRow("System.Console.WriteLine(failure?.SourceException);")]
    [DataRow("_ = failure?.ToString();")]
    public async Task AcceptsExplicitRethrowGuardsAndOtherConditionalCalls(string body)
    {
        Assert.IsEmpty(await CodeQlFileCompilation.AnalyzeAsync(CreateSource(body),
            new CodeQlUselessAssignmentToLocalAnalyzer(), TestContext.CancellationToken).ConfigureAwait(false));
    }

    private static string CreateSource(string body) => $$"""
        #nullable enable
        using System;
        using System.Runtime.ExceptionServices;
        internal static class Reader
        {
            internal static T Read<T>(Func<T> read, ExceptionDispatchInfo? failure)
            {
                T result;
                try { result = read(); }
                catch { Check(failure); throw; }
                Check(failure);
                return result;
            }
            private static void Check(ExceptionDispatchInfo? failure) { {{body}} }
        }
        """;
}
