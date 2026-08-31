using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies C# folding ranges through a real language-server worker.
/// </summary>
[TestClass]
public sealed class FoldingRangeLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Returns exact syntax, import, comment, and region folds from the current overlay.
    /// </summary>
    [TestMethod]
    public async Task FoldingRangesReflectCurrentDocumentStructure()
    {
        string fixturePath = await CreateFixtureAsync(DocumentText).ConfigureAwait(false);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            LspProcessSession lsp = await StartWorkerAsync(
                fixturePath,
                "csls-folding-range-worker").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "textDocument": {
                    "foldingRange": {
                      "rangeLimit": 32,
                      "lineFoldingOnly": false,
                      "foldingRangeKind": {
                        "valueSet": ["comment", "imports", "region"]
                      },
                      "foldingRange": {
                        "collapsedText": true
                      }
                    }
                  }
                }
                """);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(
                initialization
                    .GetProperty("capabilities")
                    .GetProperty("foldingRangeProvider")
                    .GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            IReadOnlyList<FoldingRange> ranges = await lsp.RequestFoldingRangesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.HasCount(8, ranges);
            FoldingRange imports = ranges.Single(static range =>
                range.Kind == FoldingRangeKind.Imports);
            Assert.AreEqual(0, imports.StartLine);
            Assert.AreEqual(0, imports.StartCharacter);
            Assert.AreEqual(1, imports.EndLine);
            Assert.AreEqual("using ...", imports.CollapsedText);

            FoldingRange documentation = ranges.Single(static range =>
                range.Kind == FoldingRangeKind.Comment && range.StartLine == 3);
            Assert.AreEqual(5, documentation.EndLine);
            Assert.AreEqual("/// <summary>", documentation.CollapsedText);

            FoldingRange comments = ranges.Single(static range =>
                range.Kind == FoldingRangeKind.Comment && range.StartLine == 11);
            Assert.AreEqual(12, comments.EndLine);
            Assert.AreEqual(8, comments.StartCharacter);

            FoldingRange region = ranges.Single(static range =>
                range.Kind == FoldingRangeKind.Region);
            Assert.AreEqual(6, region.StartLine);
            Assert.AreEqual(22, region.EndLine);
            Assert.AreEqual("#region Execution", region.CollapsedText);

            FoldingRange method = ranges.Single(static range =>
                range.Kind is null && range.StartLine == 14);
            Assert.AreEqual(19, method.EndLine);
            Assert.AreEqual(9, method.StartCharacter);
            Assert.AreEqual(8, method.EndCharacter);
            Assert.AreEqual("...", method.CollapsedText);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = ChangedDocumentText }])
                .ConfigureAwait(false);
            Assert.IsEmpty(await lsp.RequestFoldingRangesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false));

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
    /// Honors line-only positions, supported kinds, collapsed text, and result limits.
    /// </summary>
    [TestMethod]
    public async Task FoldingRangesHonorClientCapabilities()
    {
        string fixturePath = await CreateFixtureAsync(LimitedDocumentText).ConfigureAwait(false);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            LspProcessSession lsp = await StartWorkerAsync(
                fixturePath,
                "csls-limited-folding-range-worker").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "textDocument": {
                    "foldingRange": {
                      "rangeLimit": 2,
                      "lineFoldingOnly": true,
                      "foldingRangeKind": {
                        "valueSet": ["region"]
                      }
                    }
                  }
                }
                """);
            await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, LimitedDocumentText).ConfigureAwait(false);

            IReadOnlyList<FoldingRange> ranges = await lsp.RequestFoldingRangesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.HasCount(2, ranges);
            FoldingRange region = ranges[0];
            Assert.AreEqual(FoldingRangeKind.Region, region.Kind);
            Assert.AreEqual(0, region.StartLine);
            Assert.AreEqual(7, region.EndLine);
            Assert.IsNull(region.StartCharacter);
            Assert.IsNull(region.EndCharacter);
            Assert.IsNull(region.CollapsedText);
            Assert.IsNull(ranges[1].Kind);
            Assert.IsNull(ranges[1].StartCharacter);
            Assert.IsNull(ranges[1].EndCharacter);

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
    /// Folds multiline lists, strings, collection expressions, and conditional branches.
    /// </summary>
    [TestMethod]
    public async Task FoldingRangesIncludeMultilineExpressionsAndDirectives()
    {
        string fixturePath = await CreateFixtureAsync(ExpressionDocumentText).ConfigureAwait(false);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            LspProcessSession lsp = await StartWorkerAsync(
                fixturePath,
                "csls-expression-folding-range-worker").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, ExpressionDocumentText).ConfigureAwait(false);

            IReadOnlyList<FoldingRange> ranges = await lsp.RequestFoldingRangesAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsGreaterThanOrEqualTo(9, ranges.Count);
            Assert.IsNotNull(ranges.SingleOrDefault(static range =>
                range.Kind is null && range.StartLine == 4 && range.EndLine == 6));
            Assert.IsNotNull(ranges.SingleOrDefault(static range =>
                range.Kind is null && range.StartLine == 10 && range.EndLine == 13));
            Assert.IsNotNull(ranges.SingleOrDefault(static range =>
                range.Kind == FoldingRangeKind.Region &&
                range.StartLine == 8 && range.EndLine == 14));
            Assert.IsNotNull(ranges.SingleOrDefault(static range =>
                range.Kind == FoldingRangeKind.Region &&
                range.StartLine == 14 && range.EndLine == 20));
            Assert.IsNotNull(ranges.SingleOrDefault(static range =>
                range.Kind is null && range.StartLine == 21 && range.EndLine == 23));
            Assert.IsNotNull(ranges.SingleOrDefault(static range =>
                range.Kind is null && range.StartLine == 24 && range.EndLine == 26));
            Assert.IsNotNull(ranges.SingleOrDefault(static range =>
                range.Kind is null && range.StartLine == 27 && range.EndLine == 29));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static async Task<LspProcessSession> StartWorkerAsync(string fixturePath, string displayName)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        return await LspProcessSession.StartAsync(
            displayName,
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            fixturePath).ConfigureAwait(false);
    }

    private async Task<string> CreateFixtureAsync(string documentText)
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-folding-ranges-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Fixture.csproj"),
            ProjectText,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Program.cs"),
            documentText,
            TestContext.CancellationToken).ConfigureAwait(false);
        return fixturePath;
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        using System;
        using System.Collections.Generic;

        /// <summary>
        /// Runs the fixture.
        /// </summary>
        #region Execution
        namespace Fixture
        {
            public sealed class Program
            {
                // First line.
                // Second line.
                public void Run()
                {
                    if (true)
                    {
                        Console.WriteLine();
                    }
                }
            }
        }
        #endregion
        """;

    private const string ChangedDocumentText = """
        namespace Fixture;
        """;

    private const string LimitedDocumentText = """
        #region Fixture
        namespace Fixture
        {
            public sealed class Program
            {
                public int Value { get; init; }
            }
        }
        #endregion
        """;

    private const string ExpressionDocumentText = """"
        namespace Fixture;

        public static class Program
        {
            public static string Run(
                int first,
                int second)
            {
        #if DEBUG
                int[] values =
                [
                    first,
                    second
                ];
        #else
                int[] values =
                [
                    second,
                    first
                ];
        #endif
                string raw = """
                    value
                    """;
                string interpolated = $"""
                    {first}
                    """;
                return string.Join(
                    ",",
                    values);
            }
        }
        """";
}
