using Csls.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Compares csls behavior with the current upstream language-server oracle.
/// </summary>
[TestClass]
public sealed class UpstreamParityTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies initialization, synchronization, and hover semantics against csharp-ls.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task HoverMatchesCsharpLsOracle()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        await CoreLanguageFeaturesMatchOracleAsync(
            "csharp-ls-parity-oracle",
            EditorToolResolver.ResolveCsharpLsOracle(repositoryRoot),
            [],
            useRoslynProtocol: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies core language semantics against the Roslyn language server.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CoreLanguageFeaturesMatchRoslynLanguageServerOracle()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        await CoreLanguageFeaturesMatchOracleAsync(
            "roslyn-language-server-parity-oracle",
            EditorToolResolver.ResolveRoslynLanguageServerOracle(repositoryRoot),
            [
                "--stdio",
                "--autoLoadProjects",
                "1",
                "--logLevel",
                "Error",
                "--telemetryLevel",
                "off"
            ],
            useRoslynProtocol: true).ConfigureAwait(false);
    }

    private async Task CoreLanguageFeaturesMatchOracleAsync(
        string oracleDisplayName,
        string oraclePath,
        IReadOnlyList<string> oracleArguments,
        bool useRoslynProtocol)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(oraclePath), $"Oracle not found at {oraclePath}.");

        string fixtureRoot = Path.Join(
            Path.GetTempPath(),
            $"csls-parity-{Guid.NewGuid():N}");
        string cslsWorkspacePath = Path.Join(fixtureRoot, "csls");
        string oracleWorkspacePath = Path.Join(fixtureRoot, "oracle");
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

            LspProcessSession csls = await LspProcessSession.StartAsync(
                "csls-parity",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                cslsWorkspacePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cslsCleanup = csls.ConfigureAwait(false);
            LspTestClient? oracleClient = useRoslynProtocol
                ? new LspTestClient(legacyConfiguration: null, preferredConfiguration: null)
                : null;
            LspProcessSession oracle = await LspProcessSession.StartAsync(
                oracleDisplayName,
                oraclePath,
                oracleArguments,
                oracleWorkspacePath,
                oracleClient).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable oracleCleanup = oracle.ConfigureAwait(false);

            Task<JsonElement> cslsInitializeTask = csls.InitializeAsync(
                cslsWorkspacePath,
                TestContext.CancellationToken);
            using var oracleCapabilities = JsonDocument.Parse(
                useRoslynProtocol
                    ? OracleCapabilitiesWithProgress
                    : OracleCapabilities);
            Task<JsonElement> oracleInitializeTask = oracle.InitializeAsync(
                oracleWorkspacePath,
                oracleCapabilities.RootElement,
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
            Assert.IsTrue(
                SupportsCapability(cslsInitialize, "definitionProvider"),
                $"csls did not advertise definitions: {cslsInitialize}");
            Assert.IsTrue(
                SupportsCapability(oracleInitialize, "definitionProvider"),
                $"The oracle did not advertise definitions: {oracleInitialize}");
            Assert.IsTrue(
                SupportsCapability(cslsInitialize, "referencesProvider"),
                $"csls did not advertise references: {cslsInitialize}");
            Assert.IsTrue(
                SupportsCapability(oracleInitialize, "referencesProvider"),
                $"The oracle did not advertise references: {oracleInitialize}");
            Assert.IsTrue(
                SupportsCapability(cslsInitialize, "documentSymbolProvider"),
                $"csls did not advertise document symbols: {cslsInitialize}");
            Assert.IsTrue(
                SupportsCapability(oracleInitialize, "documentSymbolProvider"),
                $"The oracle did not advertise document symbols: {oracleInitialize}");
            Assert.AreEqual(
                GetPositionEncoding(oracleInitialize),
                GetPositionEncoding(cslsInitialize));
            Assert.AreEqual(
                GetTextDocumentSyncChange(oracleInitialize),
                GetTextDocumentSyncChange(cslsInitialize));

            await Task.WhenAll(
                csls.CompleteInitializationAsync(),
                oracle.CompleteInitializationAsync()).ConfigureAwait(false);
            if (oracleClient is not null)
            {
                await WaitForWorkspaceLoadAsync(
                    oracleClient,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            await Task.WhenAll(
                csls.OpenDocumentAsync(cslsDocumentPath, DocumentText),
                oracle.OpenDocumentAsync(oracleDocumentPath, DocumentText)).ConfigureAwait(false);

            if (useRoslynProtocol)
            {
                JsonElement? projectContexts = await oracle
                    .RequestRoslynProjectContextsAsync(
                        oracleDocumentPath,
                        TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.IsTrue(
                    projectContexts.HasValue,
                    "Roslyn did not associate the opened document with a project context.");
                Assert.HasCount(
                    1,
                    projectContexts.Value.GetProperty("_vs_projectContexts").EnumerateArray());
                Assert.AreEqual(
                    0,
                    projectContexts.Value.GetProperty("_vs_defaultIndex").GetInt32());
                JsonElement projectContext = projectContexts.Value
                    .GetProperty("_vs_projectContexts")[0];
                Assert.AreEqual(
                    "Fixture (net10.0)",
                    projectContext.GetProperty("_vs_label").GetString());
                Assert.IsFalse(projectContext.GetProperty("_vs_is_miscellaneous").GetBoolean());
            }

            if (useRoslynProtocol)
            {
                Task<IReadOnlyList<DocumentSymbol>> cslsSymbolsTask =
                    csls.RequestDocumentSymbolsAsync(
                        cslsDocumentPath,
                        TestContext.CancellationToken);
                Task<IReadOnlyList<DocumentSymbol>> oracleSymbolsTask =
                    oracle.RequestDocumentSymbolsAsync(
                        oracleDocumentPath,
                        TestContext.CancellationToken);
                await Task.WhenAll(cslsSymbolsTask, oracleSymbolsTask).ConfigureAwait(false);
                IReadOnlyList<string> cslsSymbols = FlattenDocumentSymbols(
                    await cslsSymbolsTask.ConfigureAwait(false));
                IReadOnlyList<string> oracleSymbols = FlattenDocumentSymbols(
                    await oracleSymbolsTask.ConfigureAwait(false));
                AssertSequenceEqual(oracleSymbols, cslsSymbols, "document symbols");
                AssertSequenceEqual(
                    [
                        "Fixture|Fixture|Namespace|0:10-0:17",
                        "Greeter|Greeter|Class|2:20-2:27",
                        "Message : string|Message : string|Property|4:25-4:32",
                        "Greet(string) : string|Greet(string) : string|Method|6:25-6:30",
                        "Formatter(string) : string|Formatter(string) : string|Method|8:27-8:36",
                        "Program|Program|Class|11:20-11:27",
                        "Main() : void|Main() : void|Method|13:23-13:27",
                        "Echo(string) : string|Echo(string) : string|Method|15:22-15:26"
                    ],
                    cslsSymbols,
                    "expected document symbols");
            }

            Task<JsonElement?> cslsHoverTask = csls.RequestHoverAsync(
                cslsDocumentPath,
                new Position(16, 10),
                TestContext.CancellationToken);
            Task<JsonElement?> oracleHoverTask = oracle.RequestHoverAsync(
                oracleDocumentPath,
                new Position(16, 10),
                TestContext.CancellationToken);
            await Task.WhenAll(cslsHoverTask, oracleHoverTask).ConfigureAwait(false);
            JsonElement? cslsHover = await cslsHoverTask.ConfigureAwait(false);
            JsonElement? oracleHover = await oracleHoverTask.ConfigureAwait(false);

            Assert.IsTrue(cslsHover.HasValue, "csls returned no hover result.");
            JsonElement oracleHoverValue;
            if (oracleHover is JsonElement hoverValue)
            {
                oracleHoverValue = hoverValue;
            }
            else
            {
                string oracleDiagnostics = await oracle
                    .ShutdownAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                throw new AssertFailedException(
                    $"The oracle returned no hover result. Diagnostics: {oracleDiagnostics}");
            }

            string cslsContent = GetHoverContent(cslsHover.Value);
            string oracleContent = GetHoverContent(oracleHoverValue);
            Assert.Contains("Console", cslsContent, cslsHover.Value.ToString());
            Assert.Contains("Console", oracleContent, oracleHoverValue.ToString());
            const string Documentation =
                "Represents the standard input, output, and error streams for console applications.";
            Assert.Contains(Documentation, cslsContent, cslsHover.Value.ToString());
            Assert.Contains(
                Documentation,
                NormalizeMarkdownEscapes(oracleContent),
                oracleHoverValue.ToString());
            (int StartLine, int StartCharacter, int EndLine, int EndCharacter) expectedRange =
                (16, 8, 16, 15);
            (int StartLine, int StartCharacter, int EndLine, int EndCharacter)? cslsRange =
                GetHoverRange(cslsHover.Value);
            Assert.AreEqual(expectedRange, cslsRange);
            (int StartLine, int StartCharacter, int EndLine, int EndCharacter)? oracleRange =
                GetHoverRange(oracleHover.Value);
            if (oracleRange.HasValue)
            {
                Assert.AreEqual(oracleRange, cslsRange);
            }

            if (useRoslynProtocol)
            {
                Task<IReadOnlyList<Location>> cslsDefinitionsTask = csls.RequestDefinitionsAsync(
                    cslsDocumentPath,
                    new Position(16, 33),
                    TestContext.CancellationToken);
                Task<IReadOnlyList<Location>> oracleDefinitionsTask = oracle.RequestDefinitionsAsync(
                    oracleDocumentPath,
                    new Position(16, 33),
                    TestContext.CancellationToken);
                await Task.WhenAll(cslsDefinitionsTask, oracleDefinitionsTask).ConfigureAwait(false);
                IReadOnlyList<string> cslsDefinitions = NormalizeLocations(
                    await cslsDefinitionsTask.ConfigureAwait(false));
                IReadOnlyList<string> oracleDefinitions = NormalizeLocations(
                    await oracleDefinitionsTask.ConfigureAwait(false));
                AssertSequenceEqual(oracleDefinitions, cslsDefinitions, "definitions");
                AssertSequenceEqual(["2:20-2:27"], cslsDefinitions, "expected definitions");

                Task<IReadOnlyList<Location>> cslsReferencesTask = csls.RequestReferencesAsync(
                    cslsDocumentPath,
                    new Position(2, 22),
                    includeDeclaration: true,
                    TestContext.CancellationToken);
                Task<IReadOnlyList<Location>> oracleReferencesTask = oracle.RequestReferencesAsync(
                    oracleDocumentPath,
                    new Position(2, 22),
                    includeDeclaration: true,
                    TestContext.CancellationToken);
                await Task.WhenAll(cslsReferencesTask, oracleReferencesTask).ConfigureAwait(false);
                IReadOnlyList<string> cslsReferences = NormalizeLocations(
                    await cslsReferencesTask.ConfigureAwait(false));
                IReadOnlyList<string> oracleReferences = NormalizeLocations(
                    await oracleReferencesTask.ConfigureAwait(false));
                AssertSequenceEqual(oracleReferences, cslsReferences, "references");
                AssertSequenceEqual(
                    ["16:31-16:38", "2:20-2:27"],
                    cslsReferences,
                    "expected references");
            }

            Task<string> cslsShutdownTask = csls.ShutdownAsync(TestContext.CancellationToken);
            Task<string> oracleShutdownTask = oracle.ShutdownAsync(TestContext.CancellationToken);
            await Task.WhenAll(cslsShutdownTask, oracleShutdownTask).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                await cslsShutdownTask.ConfigureAwait(false),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Unhandled exception",
                await oracleShutdownTask.ConfigureAwait(false),
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixtureRoot, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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

    private const string SolutionText = """
        <Solution>
          <Project Path="Fixture.csproj" />
        </Solution>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Greeter
        {
            public static string Message => "hello";

            public static string Greet(string name) => name;

            public delegate string Formatter(string value);
        }

        public static class Program
        {
            public static void Main()
            {
                static string Echo(string value) => value;
                Console.WriteLine(Echo(Greeter.Message));
            }
        }
        """;

    private const string OracleCapabilities = """
        {
          "textDocument": {
            "diagnostic": {},
            "documentSymbol": {
              "hierarchicalDocumentSymbolSupport": true
            },
            "hover": {
              "contentFormat": ["markdown", "plaintext"]
            }
          }
        }
        """;

    private const string OracleCapabilitiesWithProgress = """
        {
          "window": {
            "workDoneProgress": true
          },
          "textDocument": {
            "diagnostic": {},
            "documentSymbol": {
              "hierarchicalDocumentSymbolSupport": true
            },
            "hover": {
              "contentFormat": ["markdown", "plaintext"]
            }
          }
        }
        """;

    private static async Task WaitForWorkspaceLoadAsync(
        LspTestClient client,
        CancellationToken cancellationToken)
    {
        WorkDoneProgressCreateParams creation = await client
            .ReadWorkDoneProgressCreationAsync(cancellationToken)
            .ConfigureAwait(false);
        for (int progressCount = 0; progressCount < 10_000; progressCount++)
        {
            WorkDoneProgressParams progress = await client
                .ReadWorkDoneProgressAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(progress.Token, creation.Token, StringComparison.Ordinal) &&
                progress.Value is WorkDoneProgressEnd)
            {
                return;
            }
        }

        Assert.Fail("The oracle did not complete its workspace-load progress sequence.");
    }

    private static async Task<string> CreateWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        string projectPath = Path.Join(workspacePath, "Fixture.csproj");
        string solutionPath = Path.Join(workspacePath, "Fixture.slnx");
        string documentPath = Path.Join(workspacePath, "Program.cs");
        await File.WriteAllTextAsync(
            projectPath,
            ProjectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            solutionPath,
            SolutionText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            DocumentText,
            cancellationToken).ConfigureAwait(false);
        await RestoreWorkspaceAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        return documentPath;
    }

    private static async Task RestoreWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add("Fixture.csproj");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The parity fixture restore did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Parity fixture restore failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private static JsonElement GetCapabilities(JsonElement initializeResult) =>
        initializeResult.GetProperty("capabilities");

    private static bool SupportsHover(JsonElement initializeResult)
    {
        JsonElement capability = GetCapabilities(initializeResult).GetProperty("hoverProvider");
        return capability.ValueKind is JsonValueKind.True or JsonValueKind.Object;
    }

    private static bool SupportsCapability(
        JsonElement initializeResult,
        string capabilityName)
    {
        JsonElement capabilities = GetCapabilities(initializeResult);
        return capabilities.TryGetProperty(capabilityName, out JsonElement capability) &&
            capability.ValueKind is JsonValueKind.True or JsonValueKind.Object;
    }

    private static List<string> FlattenDocumentSymbols(
        IReadOnlyList<DocumentSymbol> symbols)
    {
        var flattened = new List<string>();
        AppendDocumentSymbols(symbols, flattened);
        return flattened;
    }

    private static void AppendDocumentSymbols(
        IReadOnlyList<DocumentSymbol> symbols,
        List<string> flattened)
    {
        foreach (DocumentSymbol symbol in symbols)
        {
            flattened.Add(
                $"{symbol.Name}|{symbol.Detail}|{symbol.Kind}|" +
                NormalizeRange(
                    symbol.SelectionRange.Start.Line,
                    symbol.SelectionRange.Start.Character,
                    symbol.SelectionRange.End.Line,
                    symbol.SelectionRange.End.Character));
            if (symbol.Children is not null)
            {
                AppendDocumentSymbols(symbol.Children, flattened);
            }
        }
    }

    private static IReadOnlyList<string> NormalizeLocations(
        IReadOnlyList<Location> locations) =>
        [
            .. locations
                .Select(static location => NormalizeRange(
                    location.Range.Start.Line,
                    location.Range.Start.Character,
                    location.Range.End.Line,
                    location.Range.End.Character))
                .Order(StringComparer.Ordinal)
        ];

    private static string NormalizeRange(
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter) =>
        $"{startLine}:{startCharacter}-{endLine}:{endCharacter}";

    private static string NormalizeMarkdownEscapes(string value) =>
        value.Replace("\\.", ".", StringComparison.Ordinal);

    private static void AssertSequenceEqual(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> actual,
        string description)
    {
        string message =
            $"{description}{Environment.NewLine}Expected: {string.Join(", ", expected)}" +
            $"{Environment.NewLine}Actual: {string.Join(", ", actual)}";
        Assert.HasCount(expected.Count, actual, message);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.AreEqual(expected[index], actual[index], $"{message} at index {index}");
        }
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
