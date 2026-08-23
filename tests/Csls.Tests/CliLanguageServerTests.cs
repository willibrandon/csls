using Csls.Control;
using Csls.Protocol;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies the Native AOT-compatible CLI launcher against a real language-server session.
/// </summary>
[TestClass]
public sealed class CliLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Ignores a real session socket whose peer disconnects during the first RPC.
    /// </summary>
    [TestMethod]
    public async Task SessionDiscoveryIgnoresPeerThatDisconnectsDuringHandshake()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string cliPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        string socketDirectory = ControlEndpoint.GetSocketDirectory();
        Directory.CreateDirectory(socketDirectory);
        string socketPath = Path.Combine(
            socketDirectory,
            $"disconnect-{Guid.NewGuid():N}.csls.socket");
        var listener = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        Task disconnectTask = AcceptAndDisconnectAsync(
            listener,
            TestContext.CancellationToken);
        try
        {
            (int exitCode, string output, string error) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                repositoryRoot,
                ["sessions", "list", "--json"],
                TestContext.CancellationToken).ConfigureAwait(false);
            await disconnectTask.ConfigureAwait(false);

            Assert.AreEqual(0, exitCode, $"{error}{Environment.NewLine}{output}");
            using var document = JsonDocument.Parse(output);
            AssertSuccessfulEnvelope(document.RootElement);
        }
        finally
        {
            await disconnectTask.ConfigureAwait(false);
            listener.Dispose();
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
        }
    }

    /// <summary>
    /// Lists, inspects, and queries one real session through the public csls command tree.
    /// </summary>
    [TestMethod]
    public async Task CliUsesVersionedControlServicesForLiveSession()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Environment.GetEnvironmentVariable("CSLS_TEST_CLI_PATH") ??
            Path.Combine(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.App",
                "debug",
                "csls.dll");
        string cliWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_CLI_WORKER_PATH") ??
            Path.Combine(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Cli.Worker",
                "debug",
                "csls-cli-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(cliPath), $"CLI launcher not found at {cliPath}.");
        Assert.IsTrue(File.Exists(cliWorkerPath), $"CLI worker not found at {cliWorkerPath}.");

        string fixturePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Combine(fixturePath, "Program.cs");
            string importsPath = Path.Combine(fixturePath, "Imports.cs");
            string formattingPath = Path.Combine(fixturePath, "Formatting.cs");
            string advancedPath = Path.Combine(fixturePath, "Advanced.cs");
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                importsPath,
                ImportsText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                formattingPath,
                FormattingText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                advancedPath,
                AdvancedDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-cli-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);

            string processId = lsp.ProcessId.ToString(CultureInfo.InvariantCulture);
            (int listExitCode, string listOutput, string listError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                ["sessions", "list", "--json"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, listExitCode, $"{listError}{Environment.NewLine}{listOutput}");
            using (var listDocument = JsonDocument.Parse(listOutput))
            {
                JsonElement listRoot = listDocument.RootElement;
                AssertSuccessfulEnvelope(listRoot);
                int[] processIds =
                [
                    .. listRoot
                        .GetProperty("data")
                        .EnumerateArray()
                        .Select(static session => session.GetProperty("processId").GetInt32())
                ];
                Assert.Contains(lsp.ProcessId, processIds);
            }

            (int showExitCode, string showOutput, string showError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                ["sessions", "show", "--session", processId, "--json"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, showExitCode, $"{showError}{Environment.NewLine}{showOutput}");
            using (var showDocument = JsonDocument.Parse(showOutput))
            {
                JsonElement showRoot = showDocument.RootElement;
                AssertSuccessfulEnvelope(showRoot);
                Assert.AreEqual(
                    lsp.ProcessId,
                    showRoot.GetProperty("data").GetProperty("processId").GetInt32());
                Assert.AreEqual(
                    fixturePath,
                    showRoot.GetProperty("data").GetProperty("workspaceRoots")[0].GetString());
            }

            (int hoverExitCode, string hoverOutput, string hoverError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                [
                    "query",
                    "hover",
                    documentPath,
                    "--line",
                    "6",
                    "--character",
                    "10",
                    "--session",
                    processId,
                    "--json"
                ],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, hoverExitCode, $"{hoverError}{Environment.NewLine}{hoverOutput}");
            using (var hoverDocument = JsonDocument.Parse(hoverOutput))
            {
                JsonElement hoverRoot = hoverDocument.RootElement;
                AssertSuccessfulEnvelope(hoverRoot);
                JsonElement hoverData = hoverRoot.GetProperty("data");
                Assert.IsTrue(hoverData.GetProperty("found").GetBoolean());
                Assert.Contains(
                    "System.Console",
                    hoverData
                        .GetProperty("hover")
                        .GetProperty("contents")
                        .GetProperty("value")
                        .GetString()
                        ?? throw new InvalidDataException("The CLI returned null hover text."));
            }

            (int diagnosticExitCode, string diagnosticOutput, string diagnosticError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        "diagnostics",
                        documentPath,
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                diagnosticExitCode,
                $"{diagnosticError}{Environment.NewLine}{diagnosticOutput}");
            using (var diagnosticDocument = JsonDocument.Parse(diagnosticOutput))
            {
                JsonElement diagnosticRoot = diagnosticDocument.RootElement;
                AssertSuccessfulEnvelope(diagnosticRoot);
                JsonElement diagnosticData = diagnosticRoot.GetProperty("data");
                Assert.AreEqual("full", diagnosticData.GetProperty("kind").GetString());
                Assert.Contains(
                    "CS0103",
                    diagnosticData
                        .GetProperty("items")
                        .EnumerateArray()
                        .Select(static diagnostic =>
                            diagnostic.GetProperty("code").GetString()));
            }

            (int completionExitCode, string completionOutput, string completionError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        "completion",
                        documentPath,
                        "--line",
                        "6",
                        "--character",
                        "19",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                completionExitCode,
                $"{completionError}{Environment.NewLine}{completionOutput}");
            using (var completionDocument = JsonDocument.Parse(completionOutput))
            {
                JsonElement completionRoot = completionDocument.RootElement;
                AssertSuccessfulEnvelope(completionRoot);
                Assert.Contains(
                    "WriteLine",
                    completionRoot
                        .GetProperty("data")
                        .GetProperty("items")
                        .EnumerateArray()
                        .Select(static item => item.GetProperty("label").GetString()));
            }

            (int definitionExitCode, string definitionOutput, string definitionError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        "definition",
                        documentPath,
                        "--line",
                        "7",
                        "--character",
                        "9",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                definitionExitCode,
                $"{definitionError}{Environment.NewLine}{definitionOutput}");
            using (var definitionDocument = JsonDocument.Parse(definitionOutput))
            {
                JsonElement definitionRoot = definitionDocument.RootElement;
                AssertSuccessfulEnvelope(definitionRoot);
                JsonElement definition = definitionRoot.GetProperty("data")[0];
                Assert.AreEqual(
                    10,
                    definition.GetProperty("range").GetProperty("start").GetProperty("line")
                        .GetInt32());
                Assert.AreEqual(
                    24,
                    definition.GetProperty("range").GetProperty("start").GetProperty("character")
                        .GetInt32());
            }

            IReadOnlyList<(
                string Command,
                int Line,
                int Character,
                int ExpectedCount,
                Position Expected)> navigation =
            [
                ("declaration", 19, 17, 1, new Position(4, 9)),
                ("type-definition", 18, 17, 1, new Position(2, 17)),
                ("implementation", 4, 10, 1, new Position(9, 16)),
                ("selection-range", 19, 17, 1, new Position(19, 15)),
                ("highlights", 18, 17, 4, new Position(18, 16))
            ];
            foreach ((
                string command,
                int line,
                int character,
                int expectedCount,
                Position expected) in navigation)
            {
                (int exitCode, string output, string error) = await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        command,
                        advancedPath,
                        "--line",
                        line.ToString(CultureInfo.InvariantCulture),
                        "--character",
                        character.ToString(CultureInfo.InvariantCulture),
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, exitCode, $"{error}{Environment.NewLine}{output}");
                using var navigationDocument = JsonDocument.Parse(output);
                JsonElement navigationRoot = navigationDocument.RootElement;
                AssertSuccessfulEnvelope(navigationRoot);
                JsonElement data = navigationRoot.GetProperty("data");
                Assert.AreEqual(expectedCount, data.GetArrayLength());
                JsonElement location = data[0];
                JsonElement start = location.GetProperty("range").GetProperty("start");
                Assert.AreEqual(expected.Line, start.GetProperty("line").GetInt32());
                Assert.AreEqual(
                    expected.Character,
                    start.GetProperty("character").GetInt32());
            }

            (int referencesExitCode, string referencesOutput, string referencesError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        "references",
                        documentPath,
                        "--line",
                        "7",
                        "--character",
                        "9",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                referencesExitCode,
                $"{referencesError}{Environment.NewLine}{referencesOutput}");
            using (var referencesDocument = JsonDocument.Parse(referencesOutput))
            {
                JsonElement referencesRoot = referencesDocument.RootElement;
                AssertSuccessfulEnvelope(referencesRoot);
                JsonElement references = referencesRoot.GetProperty("data");
                Assert.AreEqual(1, references.GetArrayLength());
                Assert.AreEqual(
                    7,
                    references[0].GetProperty("range").GetProperty("start").GetProperty("line")
                        .GetInt32());
            }

            (int documentSymbolsExitCode, string documentSymbolsOutput,
                string documentSymbolsError) = await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        "document-symbols",
                        documentPath,
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                documentSymbolsExitCode,
                $"{documentSymbolsError}{Environment.NewLine}{documentSymbolsOutput}");
            using (var documentSymbolsDocument = JsonDocument.Parse(documentSymbolsOutput))
            {
                JsonElement documentSymbolsRoot = documentSymbolsDocument.RootElement;
                AssertSuccessfulEnvelope(documentSymbolsRoot);
                JsonElement sourceNamespace = documentSymbolsRoot.GetProperty("data")[0];
                Assert.AreEqual("Fixture", sourceNamespace.GetProperty("name").GetString());
                Assert.Contains(
                    "Program",
                    sourceNamespace
                        .GetProperty("children")
                        .EnumerateArray()
                        .Select(static symbol => symbol.GetProperty("name").GetString()));
            }

            (int symbolsExitCode, string symbolsOutput, string symbolsError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        "symbols",
                        "Help",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                symbolsExitCode,
                $"{symbolsError}{Environment.NewLine}{symbolsOutput}");
            using (var symbolsDocument = JsonDocument.Parse(symbolsOutput))
            {
                JsonElement symbolsRoot = symbolsDocument.RootElement;
                AssertSuccessfulEnvelope(symbolsRoot);
                JsonElement helper = symbolsRoot
                    .GetProperty("data")
                    .EnumerateArray()
                    .Single(static symbol => symbol.GetProperty("name").GetString() == "Helper");
                JsonElement helperStart = helper
                    .GetProperty("location")
                    .GetProperty("range")
                    .GetProperty("start");
                Assert.AreEqual(10, helperStart.GetProperty("line").GetInt32());
                Assert.AreEqual(24, helperStart.GetProperty("character").GetInt32());
            }

            (int signatureExitCode, string signatureOutput, string signatureError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "query",
                        "signature-help",
                        documentPath,
                        "--line",
                        "7",
                        "--character",
                        "15",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                signatureExitCode,
                $"{signatureError}{Environment.NewLine}{signatureOutput}");
            using (var signatureDocument = JsonDocument.Parse(signatureOutput))
            {
                JsonElement signatureRoot = signatureDocument.RootElement;
                AssertSuccessfulEnvelope(signatureRoot);
                JsonElement signatures = signatureRoot
                    .GetProperty("data")
                    .GetProperty("signatures");
                Assert.Contains(
                    "Helper",
                    signatures
                        .EnumerateArray()
                        .Select(static signature =>
                            signature.GetProperty("label").GetString()?.Contains(
                                "Helper",
                                StringComparison.Ordinal) == true
                                ? "Helper"
                                : string.Empty));
            }

            (int renameExitCode, string renameOutput, string renameError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "rename",
                        documentPath,
                        "RenamedHelper",
                        "--line",
                        "7",
                        "--character",
                        "10",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                renameExitCode,
                $"{renameError}{Environment.NewLine}{renameOutput}");
            using (var renameDocument = JsonDocument.Parse(renameOutput))
            {
                JsonElement renameRoot = renameDocument.RootElement;
                AssertSuccessfulEnvelope(renameRoot);
                JsonElement renameDocumentEdit = Assert.ContainsSingle(
                    renameRoot
                        .GetProperty("data")
                        .GetProperty("edit")
                        .GetProperty("documentChanges")
                        .EnumerateArray());
                Assert.AreEqual(
                    1,
                    renameDocumentEdit
                        .GetProperty("textDocument")
                        .GetProperty("version")
                        .GetInt32());
                JsonElement[] renameEdits =
                [
                    .. renameDocumentEdit.GetProperty("edits").EnumerateArray()
                ];
                Assert.HasCount(2, renameEdits);
                Assert.IsTrue(renameEdits.All(static edit =>
                    edit.GetProperty("newText").GetString() == "Renamed"));
            }

            (int formatExitCode, string formatOutput, string formatError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "format",
                        documentPath,
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                formatExitCode,
                $"{formatError}{Environment.NewLine}{formatOutput}");
            using (var formatDocument = JsonDocument.Parse(formatOutput))
            {
                JsonElement formatRoot = formatDocument.RootElement;
                AssertSuccessfulEnvelope(formatRoot);
                JsonElement formatDocumentEdit = Assert.ContainsSingle(formatRoot
                    .GetProperty("data")
                    .GetProperty("edit")
                    .GetProperty("documentChanges")
                    .EnumerateArray());
                Assert.IsNotEmpty(formatDocumentEdit
                    .GetProperty("edits")
                    .EnumerateArray());
            }

            (int actionExitCode, string actionOutput, string actionError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "code-action",
                        importsPath,
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                actionExitCode,
                $"{actionError}{Environment.NewLine}{actionOutput}");
            using (var actionDocument = JsonDocument.Parse(actionOutput))
            {
                JsonElement actionRoot = actionDocument.RootElement;
                AssertSuccessfulEnvelope(actionRoot);
                JsonElement action = Assert.ContainsSingle(
                    actionRoot.GetProperty("data").EnumerateArray());
                Assert.AreEqual(
                    "source.organizeImports",
                    action.GetProperty("action").GetProperty("kind").GetString());
                Assert.IsNotEmpty(action
                    .GetProperty("editPlan")
                    .GetProperty("edit")
                    .GetProperty("documentChanges")
                    .EnumerateArray());
            }

            (int actionApplyExitCode, string actionApplyOutput, string actionApplyError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "code-action",
                        importsPath,
                        "--apply",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                actionApplyExitCode,
                $"{actionApplyError}{Environment.NewLine}{actionApplyOutput}");
            using (var actionApplyDocument = JsonDocument.Parse(actionApplyOutput))
            {
                AssertSuccessfulEnvelope(actionApplyDocument.RootElement);
            }

            string appliedImports = await File.ReadAllTextAsync(
                importsPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("using System;", appliedImports);
            Assert.Contains("using System.Text;", appliedImports);
            Assert.IsLessThan(
                appliedImports.IndexOf("using System.Text;", StringComparison.Ordinal),
                appliedImports.IndexOf("using System;", StringComparison.Ordinal));

            (int applyExitCode, string applyOutput, string applyError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "format",
                        formattingPath,
                        "--apply",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                applyExitCode,
                $"{applyError}{Environment.NewLine}{applyOutput}");
            using (var applyDocument = JsonDocument.Parse(applyOutput))
            {
                JsonElement applyRoot = applyDocument.RootElement;
                AssertSuccessfulEnvelope(applyRoot);
                Assert.Contains(
                    formattingPath,
                    applyRoot
                        .GetProperty("data")
                        .GetProperty("documentPaths")
                        .EnumerateArray()
                        .Select(static path => path.GetString()));
            }

            string appliedFormatting = await File.ReadAllTextAsync(
                formattingPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("Add(int left, int right) => left + right", appliedFormatting);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(
        string cliPath,
        string cliWorkerPath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        bool isManagedLauncher = string.Equals(
            Path.GetExtension(cliPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedLauncher ? EditorToolResolver.ResolveDotNetHost() : cliPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        if (isManagedLauncher)
        {
            startInfo.ArgumentList.Add(cliPath);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CSLS_CLI_WORKER_PATH"] = cliWorkerPath;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls CLI process did not start.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        return (process.ExitCode, standardOutput, standardError);
    }

    private static async Task AcceptAndDisconnectAsync(
        Socket listener,
        CancellationToken cancellationToken)
    {
        using Socket connection = await listener
            .AcceptAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AssertSuccessfulEnvelope(JsonElement root)
    {
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.IsTrue(Guid.TryParse(root.GetProperty("correlationId").GetString(), out _));
        Assert.IsTrue(root.GetProperty("success").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
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
                Console.WriteLine(Missing);
                Helper(1);
            }

            private static void Helper(int value)
            {
                Console.WriteLine( value );
            }
        }
        """;

    private const string ImportsText = """
        using System.Text;
        using System;

        namespace Fixture;

        public static class Imports;
        """;

    private const string FormattingText = """
        namespace Fixture;

        public static class Formatting{public static int Add(int left,int right)=>left+right;}
        """;

    private const string AdvancedDocumentText = """
        namespace Fixture;

        public interface IRunner
        {
            void Execute();
        }

        public sealed class Runner : IRunner
        {
            public void Execute()
            {
            }
        }

        public static class AdvancedProgram
        {
            public static void Run()
            {
                IRunner runner = new Runner();
                runner.Execute();
                runner = new Runner();
                _ = runner;
            }
        }
        """;
}
