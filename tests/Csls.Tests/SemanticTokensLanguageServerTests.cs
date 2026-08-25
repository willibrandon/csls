using Csls.Protocol;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies complete and delta semantic tokens through a real language-server worker.
/// </summary>
[TestClass]
public sealed class SemanticTokensLanguageServerTests
{
    private const int LongLineVariableCount = 1_000;

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Tracks exact Roslyn token classifications across complete, delta, and fallback requests.
    /// </summary>
    [TestMethod]
    public async Task SemanticTokensFullAndDeltaTrackRealDocumentChanges()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-semantic-tokens-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Tokens.cs");
            string otherDocumentPath = Path.Join(fixturePath, "Other.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                OriginalDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                otherDocumentPath,
                OtherDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-semantic-tokens-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement provider = initialization
                .GetProperty("capabilities")
                .GetProperty("semanticTokensProvider");
            Assert.IsTrue(provider.GetProperty("full").GetProperty("delta").GetBoolean());
            Assert.IsFalse(provider.GetProperty("range").GetBoolean());
            IReadOnlyList<string> tokenTypes =
            [
                .. provider
                    .GetProperty("legend")
                    .GetProperty("tokenTypes")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!)
            ];
            IReadOnlyList<string> tokenModifiers =
            [
                .. provider
                    .GetProperty("legend")
                    .GetProperty("tokenModifiers")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!)
            ];
            AssertStringSequence(CSharpSemanticTokensLegend.TokenTypes, tokenTypes);
            AssertStringSequence(CSharpSemanticTokensLegend.TokenModifiers, tokenModifiers);

            await lsp.OpenDocumentAsync(documentPath, OriginalDocumentText).ConfigureAwait(false);
            SemanticTokens original = await lsp.RequestSemanticTokensAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(original.ResultId);
            Assert.AreEqual(0, original.Data.Count % 5);
            List<(
                int Line,
                int Start,
                int Length,
                string TokenType,
                IReadOnlyList<string> Modifiers)> originalTokens = DecodeTokens(
                    original.Data,
                    tokenTypes,
                    tokenModifiers);
            AssertToken(originalTokens, 0, 10, 6, "namespace");
            AssertToken(originalTokens, 5, 20, 10, "class");
            AssertToken(originalTokens, 7, 22, 3, "method", "static");
            AssertToken(originalTokens, 7, 30, 4, "parameter");
            AssertToken(originalTokens, 9, 12, 3, "variable");

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = ChangedDocumentText }])
                .ConfigureAwait(false);
            SemanticTokensDeltaResult changedDelta = await lsp.RequestSemanticTokensDeltaAsync(
                documentPath,
                original.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(changedDelta.ResultId);
            Assert.IsNull(changedDelta.Data);
            Assert.IsNotNull(changedDelta.Edits);
            Assert.HasCount(1, changedDelta.Edits);
            IReadOnlyList<int> reconstructed = ApplyEdits(original.Data, changedDelta.Edits);

            SemanticTokens changedFull = await lsp.RequestSemanticTokensAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertIntSequence(changedFull.Data, reconstructed);
            IReadOnlyList<(
                int Line,
                int Start,
                int Length,
                string TokenType,
                IReadOnlyList<string> Modifiers)> changedTokens = DecodeTokens(
                    reconstructed,
                    tokenTypes,
                    tokenModifiers);
            AssertToken(changedTokens, 7, 15, 5, "property");
            AssertToken(changedTokens, 9, 22, 3, "method", "static");

            SemanticTokensDeltaResult unchangedDelta = await lsp.RequestSemanticTokensDeltaAsync(
                documentPath,
                changedDelta.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(unchangedDelta.ResultId);
            Assert.IsNull(unchangedDelta.Data);
            Assert.IsNotNull(unchangedDelta.Edits);
            Assert.IsEmpty(unchangedDelta.Edits);

            SemanticTokensDeltaResult fallback = await lsp.RequestSemanticTokensDeltaAsync(
                documentPath,
                "unknown-result",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(fallback.ResultId);
            Assert.IsNotNull(fallback.Data);
            Assert.IsNull(fallback.Edits);
            AssertIntSequence(changedFull.Data, fallback.Data);

            await lsp.OpenDocumentAsync(otherDocumentPath, OtherDocumentText).ConfigureAwait(false);
            SemanticTokensDeltaResult crossDocumentFallback =
                await lsp.RequestSemanticTokensDeltaAsync(
                    otherDocumentPath,
                    unchangedDelta.ResultId,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(crossDocumentFallback.ResultId);
            Assert.IsNotNull(crossDocumentFallback.Data);
            Assert.IsNull(crossDocumentFallback.Edits);
            IReadOnlyList<(
                int Line,
                int Start,
                int Length,
                string TokenType,
                IReadOnlyList<string> Modifiers)> otherTokens = DecodeTokens(
                    crossDocumentFallback.Data,
                    tokenTypes,
                    tokenModifiers);
            AssertToken(otherTokens, 2, 20, 5, "class");

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Normalizes a large single-line classification set without losing declaration tokens.
    /// </summary>
    [TestMethod]
    public async Task SemanticTokensPreserveLargeSingleLineDocuments()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-semantic-tokens-long-line-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "LongLine.cs");
            string documentText = CreateLongLineDocument();
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                documentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-semantic-tokens-long-line-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement legend = initialization
                .GetProperty("capabilities")
                .GetProperty("semanticTokensProvider")
                .GetProperty("legend");
            IReadOnlyList<string> tokenTypes =
            [
                .. legend
                    .GetProperty("tokenTypes")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!)
            ];
            IReadOnlyList<string> tokenModifiers =
            [
                .. legend
                    .GetProperty("tokenModifiers")
                    .EnumerateArray()
                    .Select(static item => item.GetString()!)
            ];
            await lsp.OpenDocumentAsync(documentPath, documentText).ConfigureAwait(false);

            SemanticTokens result = await lsp.RequestSemanticTokensAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            List<(
                int Line,
                int Start,
                int Length,
                string TokenType,
                IReadOnlyList<string> Modifiers)> tokens = DecodeTokens(
                    result.Data,
                    tokenTypes,
                    tokenModifiers);
            Assert.IsGreaterThan(LongLineVariableCount, tokens.Count);
            AssertLongLineVariable(tokens, documentText, 0);
            AssertLongLineVariable(tokens, documentText, LongLineVariableCount / 2);
            AssertLongLineVariable(tokens, documentText, LongLineVariableCount - 1);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static void AssertLongLineVariable(
        IReadOnlyList<(
            int Line,
            int Start,
            int Length,
            string TokenType,
            IReadOnlyList<string> Modifiers)> tokens,
        string documentText,
        int index)
    {
        string name = $"value{index}";
        int start = documentText.IndexOf(name, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        AssertToken(tokens, 0, start, name.Length, "variable");
    }

    private static string CreateLongLineDocument()
    {
        var builder = new StringBuilder(LongLineVariableCount * 24);
        builder.Append(
            "namespace Tokens; public static class LongLineTarget { " +
            "public static int Measure() { ");
        for (int index = 0; index < LongLineVariableCount; index++)
        {
            string indexText = index.ToString(CultureInfo.InvariantCulture);
            builder.Append("int value");
            builder.Append(indexText);
            builder.Append(" = ");
            builder.Append(indexText);
            builder.Append("; ");
        }

        builder.Append("return value");
        builder.Append((LongLineVariableCount - 1).ToString(CultureInfo.InvariantCulture));
        builder.Append("; } }");
        return builder.ToString();
    }

    private static List<(
        int Line,
        int Start,
        int Length,
        string TokenType,
        IReadOnlyList<string> Modifiers)> DecodeTokens(
            IReadOnlyList<int> data,
            IReadOnlyList<string> tokenTypes,
            IReadOnlyList<string> tokenModifiers)
    {
        var tokens = new List<(
            int Line,
            int Start,
            int Length,
            string TokenType,
            IReadOnlyList<string> Modifiers)>(data.Count / 5);
        int line = 0;
        int start = 0;
        for (int index = 0; index < data.Count; index += 5)
        {
            int deltaLine = data[index];
            line += deltaLine;
            start = deltaLine == 0 ? start + data[index + 1] : data[index + 1];
            int tokenTypeIndex = data[index + 3];
            if (tokenTypeIndex < 0 || tokenTypeIndex >= tokenTypes.Count)
            {
                throw new InvalidDataException($"Unknown token type index {tokenTypeIndex}.");
            }

            int modifierBits = data[index + 4];
            var modifiers = new List<string>();
            for (int modifierIndex = 0;
                modifierIndex < tokenModifiers.Count;
                modifierIndex++)
            {
                if ((modifierBits & (1 << modifierIndex)) != 0)
                {
                    modifiers.Add(tokenModifiers[modifierIndex]);
                }
            }

            if ((modifierBits >> tokenModifiers.Count) != 0)
            {
                throw new InvalidDataException($"Unknown token modifier bits {modifierBits}.");
            }

            tokens.Add((
                line,
                start,
                data[index + 2],
                tokenTypes[tokenTypeIndex],
                modifiers));
        }

        return tokens;
    }

    private static List<int> ApplyEdits(
        IReadOnlyList<int> previous,
        IReadOnlyList<SemanticTokensEdit> edits)
    {
        List<int> current = [.. previous];
        foreach (SemanticTokensEdit edit in edits.OrderByDescending(static edit => edit.Start))
        {
            current.RemoveRange(edit.Start, edit.DeleteCount);
            if (edit.Data is not null)
            {
                current.InsertRange(edit.Start, edit.Data);
            }
        }

        return current;
    }

    private static void AssertToken(
        IReadOnlyList<(
            int Line,
            int Start,
            int Length,
            string TokenType,
            IReadOnlyList<string> Modifiers)> tokens,
        int line,
        int start,
        int length,
        string tokenType,
        string? modifier = null)
    {
        (int Line,
            int Start,
            int Length,
            string TokenType,
            IReadOnlyList<string> Modifiers) token = Assert.ContainsSingle(
                tokens.Where(candidate =>
                    candidate.Line == line &&
                    candidate.Start == start &&
                    candidate.Length == length));
        Assert.AreEqual(tokenType, token.TokenType);
        if (modifier is not null)
        {
            Assert.Contains(modifier, token.Modifiers);
        }
    }

    private static void AssertIntSequence(
        IReadOnlyList<int> expected,
        IReadOnlyList<int> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"Integer index {index} differs.");
        }
    }

    private static void AssertStringSequence(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual)
    {
        Assert.HasCount(expected.Count, actual);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"String index {index} differs.");
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string OriginalDocumentText = """
        namespace Tokens;

        /// <summary>
        /// Adds values.
        /// </summary>
        public sealed class Calculator
        {
            public static int Add(int left, int right)
            {
                int sum = left + right;
                return sum;
            }
        }
        """;

    private const string ChangedDocumentText = """
        namespace Tokens;

        /// <summary>
        /// Adds values.
        /// </summary>
        public sealed class Calculator
        {
            public int Value { get; set; }

            public static int Add(int left, int right)
            {
                int sum = left + right;
                return sum;
            }
        }
        """;

    private const string OtherDocumentText = """
        namespace Tokens;

        public sealed class Other
        {
        }
        """;
}
