using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Globalization;

namespace Csls.SourceGen.Tests;

/// <summary>
/// Verifies dictionary guard diagnostics through file-backed semantic compilations.
/// </summary>
/// <param name="testContext">The test cancellation context.</param>
[TestClass]
public sealed class CodeQlInefficientContainsKeyAnalyzerTests(TestContext testContext)
{
    /// <summary>
    /// Rejects repeated reads after conditional, exit, and asynchronous receive-loop guards.
    /// </summary>
    /// <param name="body">The redundant guarded lookup.</param>
    [TestMethod]
    [DataRow("if (values.ContainsKey(key)) return values[key]; return 0;")]
    [DataRow("if (!values.ContainsKey(key)) return 0; return values[key];")]
    [DataRow("if (!values.ContainsKey(key)) return 0; else return values[key];")]
    [DataRow("return values.ContainsKey(key) ? values[key] : 0;")]
    [DataRow("return !values.ContainsKey(key) ? 0 : values[key];")]
    [DataRow("while (!values.ContainsKey(key)) { await System.Threading.Tasks.Task.Yield(); } int result = values[key]; values.Remove(key); return result;")]
    [DataRow("while (values.ContainsKey(key)) { return values[key]; } return 0;")]
    [DataRow("if ((values.ContainsKey(key))) { return values[key]; } return 0;")]
    [DataRow("if (values.ContainsKey(1)) return values[1]; return 0;")]
    public async Task ReportsGuardedDictionaryRead(string body)
    {
        string source = Source(body);
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(CodeQlInefficientContainsKeyAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(source.IndexOf("values.ContainsKey", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("Retrieve the dictionary value with TryGetValue or Remove instead of ContainsKey followed by an indexer",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Resolves dictionary contracts through concrete and interface receivers.
    /// </summary>
    /// <param name="type">The receiver's declared dictionary type.</param>
    [TestMethod]
    [DataRow("System.Collections.Generic.IDictionary<int, int>")]
    [DataRow("System.Collections.Generic.IReadOnlyDictionary<int, int>")]
    [DataRow("System.Collections.Concurrent.ConcurrentDictionary<int, int>")]
    [DataRow("System.Collections.ObjectModel.ReadOnlyDictionary<int, int>")]
    public async Task ReportsDictionaryContractRead(string type)
    {
        string source = Source("if (values.ContainsKey(key)) return values[key]; return 0;", type);
        Diagnostic diagnostic = Assert.ContainsSingle(await AnalyzeAsync(source).ConfigureAwait(false));
        Assert.AreEqual(CodeQlInefficientContainsKeyAnalyzer.DiagnosticId, diagnostic.Id);
        Assert.AreEqual(source.IndexOf("values.ContainsKey", StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Accepts one-lookup retrieval, distinct keys or receivers, writes, mutations, and deferred reads.
    /// </summary>
    /// <param name="body">The operation whose lookup is independent or already combined.</param>
    [TestMethod]
    [DataRow("if (values.TryGetValue(key, out int result)) return result; return 0;")]
    [DataRow("int result; while (!values.Remove(key, out result)) { await System.Threading.Tasks.Task.Yield(); } return result;")]
    [DataRow("if (values.ContainsKey(key)) return values[key + 1]; return 0;")]
    [DataRow("if (values.ContainsKey(key)) return other[key]; return 0;")]
    [DataRow("if (values.ContainsKey(key)) values[key] = 42; return 0;")]
    [DataRow("if (values.ContainsKey(key)) (values[key]) = 42; return 0;")]
    [DataRow("if (values.ContainsKey(key)) { key++; return values[key]; } return 0;")]
    [DataRow("if (values.ContainsKey(key)) { values = other; return values[key]; } return 0;")]
    [DataRow("if (values.ContainsKey(key)) return (key = 3) + values[key]; return 0;")]
    [DataRow("if (values.ContainsKey(key)) { System.Func<int> read = () => values[key]; return read(); } return 0;")]
    [DataRow("if (values.ContainsKey(key)) { int Read() => values[key]; return Read(); } return 0;")]
    [DataRow("bool present = values.ContainsKey(key); await System.Threading.Tasks.Task.Yield(); return present ? values[key] : 0;")]
    [DataRow("if (values.ContainsKey(key)) return 1; return values[key];")]
    [DataRow("while (!values.ContainsKey(key)) { break; } return values[key];")]
    public async Task AcceptsIndependentOrCombinedLookup(string body)
    {
        Assert.IsEmpty(await AnalyzeAsync(Source(body)).ConfigureAwait(false));
    }

    /// <summary>
    /// Distinguishes identical field symbols accessed through different object instances.
    /// </summary>
    /// <param name="sameReceiver">Whether the indexer reads the guarded object.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreservesFieldReceiverIdentity(bool sameReceiver)
    {
        string receiver = sameReceiver ? "this" : "other";
        string source = $$"""
            using System.Collections.Generic;
            internal sealed class Lookup
            {
                private readonly Dictionary<int, int> _values = new();
                internal int Read(int key, Lookup other)
                {
                    if (_values.ContainsKey(key)) return {{receiver}}._values[key];
                    return 0;
                }
            }
            """;
        ImmutableArray<Diagnostic> diagnostics = await AnalyzeAsync(source).ConfigureAwait(false);
        if (sameReceiver)
        {
            Assert.AreEqual(CodeQlInefficientContainsKeyAnalyzer.DiagnosticId, Assert.ContainsSingle(diagnostics).Id);
        }
        else
        {
            Assert.IsEmpty(diagnostics);
        }
    }

    /// <summary>
    /// Accepts methods and indexers whose names resemble a dictionary without implementing its contract.
    /// </summary>
    [TestMethod]
    public async Task AcceptsNonDictionaryMembers()
    {
        const string SourceText = """
            internal sealed class Lookup
            {
                internal bool ContainsKey(int key) => key > 0;
                internal int this[int key] => key;
                internal int Read(Lookup values, int key) => values.ContainsKey(key) ? values[key] : 0;
            }
            """;
        Assert.IsEmpty(await AnalyzeAsync(SourceText).ConfigureAwait(false));
    }

    /// <summary>
    /// Preserves a derived indexer whose behavior is separate from the inherited dictionary contract.
    /// </summary>
    [TestMethod]
    public async Task AcceptsHiddenNonDictionaryIndexer()
    {
        const string SourceText = """
            internal sealed class Lookup : System.Collections.Generic.Dictionary<int, int>
            {
                internal new int this[int key] => key;
                internal int Read(Lookup values, int key) => values.ContainsKey(key) ? values[key] : 0;
            }
            """;
        Assert.IsEmpty(await AnalyzeAsync(SourceText).ConfigureAwait(false));
    }

    /// <summary>
    /// Checks the analyzer's own source for redundant lookups and implicit sequence filters.
    /// </summary>
    [TestMethod]
    public async Task AcceptsProductionAnalyzerImplementation()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "Csls.slnx")))
        {
            repository = repository.Parent;
        }
        Assert.IsNotNull(repository);
        string path = Path.Join(repository.FullName, "src", "Csls.SourceGen", "CodeQlInefficientContainsKeyAnalyzer.cs");
        string source = await File.ReadAllTextAsync(path, testContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(await AnalyzeAsync(source).ConfigureAwait(false));
        Assert.IsEmpty(await CodeQlFileCompilation.AnalyzeAsync(source, new CodeQlMissedWhereAnalyzer(), testContext.CancellationToken)
            .ConfigureAwait(false));
    }

    private static string Source(string body, string type = "System.Collections.Generic.Dictionary<int, int>") => $$"""
        internal static class Lookup
        {
            internal static async System.Threading.Tasks.Task<int> Read({{type}} values, int key, {{type}} other)
            {
                {{body}}
            }
        }
        """;

    private Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) =>
        CodeQlFileCompilation.AnalyzeAsync(source, new CodeQlInefficientContainsKeyAnalyzer(), testContext.CancellationToken);
}
