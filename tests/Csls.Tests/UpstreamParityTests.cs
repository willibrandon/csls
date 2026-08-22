using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Csls.Protocol;

namespace Csls.Tests;

/// <summary>
/// Compares csls behavior with the pinned upstream language-server oracle.
/// </summary>
[TestClass]
public sealed class UpstreamParityTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies initialization, synchronization, and hover semantics against upstream.
    /// </summary>
    [TestMethod]
    public async Task HoverMatchesPinnedOracle()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string oraclePath = EditorToolResolver.ResolveCsharpLsOracle(repositoryRoot);
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(oraclePath), $"Oracle not found at {oraclePath}.");

        string fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"csls-parity-{Guid.NewGuid():N}");
        string cslsWorkspacePath = Path.Combine(fixtureRoot, "csls");
        string oracleWorkspacePath = Path.Combine(fixtureRoot, "oracle");
        Directory.CreateDirectory(cslsWorkspacePath);
        Directory.CreateDirectory(oracleWorkspacePath);
        try
        {
            string cslsDocumentPath = await CreateWorkspaceAsync(
                cslsWorkspacePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            string oracleDocumentPath = await CreateWorkspaceAsync(
                oracleWorkspacePath,
                TestContext.CancellationToken).ConfigureAwait(false);

            var csls = LspProcessSession.Start(
                "csls-parity",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                cslsWorkspacePath);
            await using ConfiguredAsyncDisposable cslsCleanup = csls.ConfigureAwait(false);
            var oracle = LspProcessSession.Start(
                "csharp-ls-parity-oracle",
                oraclePath,
                [],
                oracleWorkspacePath);
            await using ConfiguredAsyncDisposable oracleCleanup = oracle.ConfigureAwait(false);

            Task<JsonElement> cslsInitializeTask = csls.InitializeAsync(
                cslsWorkspacePath,
                TestContext.CancellationToken);
            Task<JsonElement> oracleInitializeTask = oracle.InitializeAsync(
                oracleWorkspacePath,
                TestContext.CancellationToken);
            await Task.WhenAll(cslsInitializeTask, oracleInitializeTask).ConfigureAwait(false);
            JsonElement cslsInitialize = await cslsInitializeTask.ConfigureAwait(false);
            JsonElement oracleInitialize = await oracleInitializeTask.ConfigureAwait(false);

            Assert.IsTrue(
                SupportsHover(cslsInitialize),
                $"csls did not advertise hover: {cslsInitialize}");
            Assert.IsTrue(
                SupportsHover(oracleInitialize),
                $"The oracle did not advertise hover: {oracleInitialize}");
            Assert.AreEqual(
                GetPositionEncoding(oracleInitialize),
                GetPositionEncoding(cslsInitialize));
            Assert.AreEqual(
                GetTextDocumentSyncChange(oracleInitialize),
                GetTextDocumentSyncChange(cslsInitialize));

            await Task.WhenAll(
                csls.OpenDocumentAsync(cslsDocumentPath, DocumentText),
                oracle.OpenDocumentAsync(oracleDocumentPath, DocumentText)).ConfigureAwait(false);
            Task<JsonElement?> cslsHoverTask = csls.RequestHoverAsync(
                cslsDocumentPath,
                new Position(6, 10),
                TestContext.CancellationToken);
            Task<JsonElement?> oracleHoverTask = oracle.RequestHoverAsync(
                oracleDocumentPath,
                new Position(6, 10),
                TestContext.CancellationToken);
            await Task.WhenAll(cslsHoverTask, oracleHoverTask).ConfigureAwait(false);
            JsonElement? cslsHover = await cslsHoverTask.ConfigureAwait(false);
            JsonElement? oracleHover = await oracleHoverTask.ConfigureAwait(false);

            Assert.IsTrue(cslsHover.HasValue, "csls returned no hover result.");
            Assert.IsTrue(oracleHover.HasValue, "The oracle returned no hover result.");
            string cslsContent = GetHoverContent(cslsHover.Value);
            string oracleContent = GetHoverContent(oracleHover.Value);
            Assert.Contains("Console", cslsContent, cslsHover.Value.ToString());
            Assert.Contains("Console", oracleContent, oracleHover.Value.ToString());
            const string Documentation =
                "Represents the standard input, output, and error streams for console applications.";
            Assert.Contains(Documentation, cslsContent, cslsHover.Value.ToString());
            Assert.Contains(Documentation, oracleContent, oracleHover.Value.ToString());
            (int StartLine, int StartCharacter, int EndLine, int EndCharacter) expectedRange =
                (6, 8, 6, 15);
            (int StartLine, int StartCharacter, int EndLine, int EndCharacter)? cslsRange =
                GetHoverRange(cslsHover.Value);
            Assert.AreEqual(expectedRange, cslsRange);
            (int StartLine, int StartCharacter, int EndLine, int EndCharacter)? oracleRange =
                GetHoverRange(oracleHover.Value);
            if (oracleRange.HasValue)
            {
                Assert.AreEqual(oracleRange, cslsRange);
            }

            await Task.WhenAll(
                csls.ShutdownAsync(TestContext.CancellationToken),
                oracle.ShutdownAsync(TestContext.CancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
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

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("hello");
            }
        }
        """;

    private static async Task<string> CreateWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        string projectPath = Path.Combine(workspacePath, "Fixture.csproj");
        string documentPath = Path.Combine(workspacePath, "Program.cs");
        await File.WriteAllTextAsync(
            projectPath,
            ProjectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            DocumentText,
            cancellationToken).ConfigureAwait(false);
        return documentPath;
    }

    private static JsonElement GetCapabilities(JsonElement initializeResult) =>
        initializeResult.GetProperty("capabilities");

    private static bool SupportsHover(JsonElement initializeResult)
    {
        JsonElement capability = GetCapabilities(initializeResult).GetProperty("hoverProvider");
        return capability.ValueKind is JsonValueKind.True or JsonValueKind.Object;
    }

    private static string GetPositionEncoding(JsonElement initializeResult)
    {
        JsonElement capabilities = GetCapabilities(initializeResult);
        return capabilities.TryGetProperty("positionEncoding", out JsonElement encoding)
            ? encoding.GetString() ?? "utf-16"
            : "utf-16";
    }

    private static int GetTextDocumentSyncChange(JsonElement initializeResult)
    {
        JsonElement synchronization = GetCapabilities(initializeResult)
            .GetProperty("textDocumentSync");
        return synchronization.ValueKind == JsonValueKind.Number
            ? synchronization.GetInt32()
            : synchronization.GetProperty("change").GetInt32();
    }

    private static string GetHoverContent(JsonElement hover)
    {
        var content = new StringBuilder();
        AppendStrings(hover.GetProperty("contents"), content);
        return content.ToString();
    }

    private static void AppendStrings(JsonElement element, StringBuilder content)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                content.AppendLine(element.GetString());
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AppendStrings(item, content);
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    AppendStrings(property.Value, content);
                }

                break;
        }
    }

    private static (int StartLine, int StartCharacter, int EndLine, int EndCharacter)?
        GetHoverRange(JsonElement hover)
    {
        if (!hover.TryGetProperty("range", out JsonElement range))
        {
            return null;
        }

        JsonElement start = range.GetProperty("start");
        JsonElement end = range.GetProperty("end");
        return (
            start.GetProperty("line").GetInt32(),
            start.GetProperty("character").GetInt32(),
            end.GetProperty("line").GetInt32(),
            end.GetProperty("character").GetInt32());
    }
}
