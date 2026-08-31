using Csls.Control;
using Csls.Control.Contracts;
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
    /// Prunes an abandoned real Unix-domain socket before applying the live-session bound.
    /// </summary>
    [TestMethod]
    public async Task SessionDiscoveryPrunesStaleSocket()
    {
        string socketDirectory = ControlEndpoint.GetSocketDirectory();
        Directory.CreateDirectory(socketDirectory);
        string socketPath = ControlEndpoint.GetSocketPath(int.MaxValue);
        File.Delete(socketPath);

        try
        {
            using var socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            socket.Listen(1);

            Assert.Contains(
                socketPath,
                Directory.EnumerateFileSystemEntries(socketDirectory));
            IReadOnlyList<ControlSessionInfo> sessions = await ControlSessionDiscovery
                .DiscoverAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.DoesNotContain(
                socketPath,
                Directory.EnumerateFileSystemEntries(socketDirectory));
            Assert.DoesNotContain(
                int.MaxValue,
                sessions.Select(static session => session.ProcessId));
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    /// <summary>
    /// Ignores a real session socket whose peer disconnects during the first RPC.
    /// </summary>
    [TestMethod]
    public async Task SessionDiscoveryIgnoresPeerThatDisconnectsDuringHandshake()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string cliPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        string socketDirectory = ControlEndpoint.GetSocketDirectory();
        Directory.CreateDirectory(socketDirectory);
        string socketPath = Path.Join(
            socketDirectory,
            $"d-{Guid.NewGuid():N}"[..14] + ".csls.socket");
        Task disconnectTask = AcceptAndDisconnectAsync(
            socketPath,
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
            File.Delete(socketPath);
        }
    }

    /// <summary>
    /// Streams added, updated, and removed events for one real language-server session.
    /// </summary>
    [TestMethod]
    public async Task SessionWatchStreamsRealSessionChanges()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-session-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "SessionWatch.csproj"),
            ProjectText,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Program.cs"),
            "Console.WriteLine(\"watch\");",
            TestContext.CancellationToken).ConfigureAwait(false);

        using Process watchProcess = StartCliProcess(
            cliPath,
            cliWorkerPath,
            repositoryRoot,
            ["sessions", "watch", "--json"]);
        Task<string> watchErrorTask = watchProcess.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        try
        {
            JsonElement snapshot = await ReadWatchEventAsync(
                watchProcess.StandardOutput,
                static data => data.GetProperty("kind").GetString() == "Snapshot",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1L, snapshot.GetProperty("sequence").GetInt64());
            Assert.AreEqual(JsonValueKind.Null, snapshot.GetProperty("session").ValueKind);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-session-watch-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            ControlSessionInfo runningSession = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);

            JsonElement added = await ReadWatchEventAsync(
                watchProcess.StandardOutput,
                data => IsWatchEvent(data, "Added", lsp.ProcessId),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(
                snapshot.GetProperty("sequence").GetInt64(),
                added.GetProperty("sequence").GetInt64());
            Assert.Contains(
                lsp.ProcessId,
                added.GetProperty("sessions")
                    .EnumerateArray()
                    .Select(static session => session.GetProperty("processId").GetInt32()));

            var client = new ControlRpcClient(runningSession.SocketPath);
            await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
            ControlWorkspaceOperationResult reload = await client.ReloadWorkspaceAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement updated = await ReadWatchEventAsync(
                watchProcess.StandardOutput,
                data => IsWatchEvent(data, "Updated", lsp.ProcessId) &&
                    data.GetProperty("session").GetProperty("workspaceGeneration").GetInt64() ==
                    reload.CurrentGeneration,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(
                added.GetProperty("sequence").GetInt64(),
                updated.GetProperty("sequence").GetInt64());

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
            JsonElement removed = await ReadWatchEventAsync(
                watchProcess.StandardOutput,
                data => IsWatchEvent(data, "Removed", lsp.ProcessId),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(
                updated.GetProperty("sequence").GetInt64(),
                removed.GetProperty("sequence").GetInt64());
            Assert.DoesNotContain(
                lsp.ProcessId,
                removed.GetProperty("sessions")
                    .EnumerateArray()
                    .Select(static session => session.GetProperty("processId").GetInt32()));
        }
        finally
        {
            if (!watchProcess.HasExited)
            {
                watchProcess.Kill(entireProcessTree: true);
                await watchProcess.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            Directory.Delete(fixturePath, recursive: true);
        }

        string watchError = await watchErrorTask.ConfigureAwait(false);
        Assert.DoesNotContain("Unhandled exception", watchError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Creates, protects, replaces, and streams a reusable skill through the public CLI.
    /// </summary>
    [TestMethod]
    public async Task AgentInitManagesReusableSkillFile()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string cliPath = Environment.GetEnvironmentVariable("CSLS_TEST_CLI_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.App",
                "debug",
                "csls.dll");
        string cliWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_CLI_WORKER_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Cli.Worker",
                "debug",
                "csls-cli-worker.dll");
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-agent-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            (int createExitCode, string createOutput, string createError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    ["agent", "init", "--json"],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                createExitCode,
                $"{createError}{Environment.NewLine}{createOutput}");
            using var createDocument = JsonDocument.Parse(createOutput);
            AssertSuccessfulEnvelope(createDocument.RootElement);
            string outputPath = createDocument.RootElement
                .GetProperty("data")
                .GetProperty("outputPath")
                .GetString() ?? throw new InvalidDataException(
                    "Agent initialization returned no output path.");
            string expectedPath = Path.Join(fixturePath, "SKILL.md");
            Assert.IsTrue(Path.IsPathFullyQualified(outputPath));
            Assert.AreEqual("SKILL.md", Path.GetFileName(outputPath));
            Assert.IsTrue(File.Exists(expectedPath));
            Assert.IsTrue(File.Exists(outputPath));

            byte[] skillBytes = await File.ReadAllBytesAsync(
                outputPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(0, skillBytes.Length);
            Assert.AreEqual((byte)'-', skillBytes[0]);
            string skillContent = await File.ReadAllTextAsync(
                outputPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("name: csls", skillContent, StringComparison.Ordinal);
            Assert.Contains(
                "csls agent mcp --workspace .",
                skillContent,
                StringComparison.Ordinal);
            Assert.Contains(
                "Edit commands preview guarded plans by default.",
                skillContent,
                StringComparison.Ordinal);

            const string retainedContent = "retain existing instructions";
            await File.WriteAllTextAsync(
                outputPath,
                retainedContent,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                retainedContent,
                await File.ReadAllTextAsync(
                    expectedPath,
                    TestContext.CancellationToken).ConfigureAwait(false));
            (int existingExitCode, string existingOutput, string existingError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    ["agent", "init", "--json"],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                1,
                existingExitCode,
                $"{existingError}{Environment.NewLine}{existingOutput}");
            using (var existingDocument = JsonDocument.Parse(existingOutput))
            {
                Assert.IsFalse(existingDocument.RootElement.GetProperty("success").GetBoolean());
                Assert.AreEqual(
                    "file-exists",
                    existingDocument.RootElement
                        .GetProperty("data")
                        .GetProperty("code")
                        .GetString());
            }

            Assert.AreEqual(
                retainedContent,
                await File.ReadAllTextAsync(
                    outputPath,
                    TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(Directory.GetFiles(fixturePath, "*.tmp"));

            (int forceExitCode, string forceOutput, string forceError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                ["agent", "init", "--force", "--json"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                forceExitCode,
                $"{forceError}{Environment.NewLine}{forceOutput}");
            using (var forceDocument = JsonDocument.Parse(forceOutput))
            {
                AssertSuccessfulEnvelope(forceDocument.RootElement);
            }

            string replacedContent = await File.ReadAllTextAsync(
                outputPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(retainedContent, replacedContent);
            Assert.IsEmpty(Directory.GetFiles(fixturePath, "*.tmp"));

            (int stdoutExitCode, string stdoutOutput, string stdoutError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                ["agent", "init", "--stdout"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                stdoutExitCode,
                $"{stdoutError}{Environment.NewLine}{stdoutOutput}");
            Assert.AreEqual(replacedContent, stdoutOutput);

            (int conflictExitCode, _, string conflictError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                ["agent", "init", "--stdout", "--json"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(2, conflictExitCode);
            Assert.Contains(
                "--stdout cannot be combined",
                conflictError,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Lists, inspects, and queries one real session through the public csls command tree.
    /// </summary>
    [TestMethod]
    public async Task CliUsesVersionedControlServicesForLiveSession()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Environment.GetEnvironmentVariable("CSLS_TEST_CLI_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.App",
                "debug",
                "csls.dll");
        string cliWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_CLI_WORKER_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Cli.Worker",
                "debug",
                "csls-cli-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(cliPath), $"CLI launcher not found at {cliPath}.");
        Assert.IsTrue(File.Exists(cliWorkerPath), $"CLI worker not found at {cliWorkerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string importsPath = Path.Join(fixturePath, "Imports.cs");
            string missingUsingPath = Path.Join(fixturePath, "MissingUsing.cs");
            string implementInterfacePath = Path.Join(fixturePath, "ImplementInterface.cs");
            string formattingPath = Path.Join(fixturePath, "Formatting.cs");
            string advancedPath = Path.Join(fixturePath, "Advanced.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
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
                missingUsingPath,
                MissingUsingText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                implementInterfacePath,
                ImplementInterfaceText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                formattingPath,
                FormattingText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                advancedPath,
                AdvancedDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-cli-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
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

            (int quickFixExitCode, string quickFixOutput, string quickFixError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "code-action",
                        missingUsingPath,
                        "--kind",
                        "quickfix",
                        "--line",
                        "6",
                        "--character",
                        "26",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                quickFixExitCode,
                $"{quickFixError}{Environment.NewLine}{quickFixOutput}");
            using (var quickFixDocument = JsonDocument.Parse(quickFixOutput))
            {
                JsonElement quickFixRoot = quickFixDocument.RootElement;
                AssertSuccessfulEnvelope(quickFixRoot);
                JsonElement[] quickFixes =
                [
                    .. quickFixRoot.GetProperty("data").EnumerateArray()
                ];
                Assert.Contains(
                    "System.Text.StringBuilder",
                    quickFixes.Select(static candidate => candidate
                        .GetProperty("action")
                        .GetProperty("title")
                        .GetString()));
                JsonElement quickFix = Assert.ContainsSingle(quickFixes.Where(
                    static candidate => candidate
                        .GetProperty("action")
                        .GetProperty("title")
                        .GetString() == "using System.Text;"));
                Assert.AreEqual(
                    "using System.Text;",
                    quickFix.GetProperty("action").GetProperty("title").GetString());
                Assert.AreEqual(
                    "quickfix",
                    quickFix.GetProperty("action").GetProperty("kind").GetString());
                Assert.IsNotEmpty(quickFix
                    .GetProperty("editPlan")
                    .GetProperty("edit")
                    .GetProperty("documentChanges")
                    .EnumerateArray());
            }

            (int quickFixApplyExitCode, string quickFixApplyOutput, string quickFixApplyError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "code-action",
                        missingUsingPath,
                        "--kind",
                        "quickfix",
                        "--line",
                        "6",
                        "--character",
                        "26",
                        "--title",
                        "using System.Text;",
                        "--apply",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                quickFixApplyExitCode,
                $"{quickFixApplyError}{Environment.NewLine}{quickFixApplyOutput}");
            using (var quickFixApplyDocument = JsonDocument.Parse(quickFixApplyOutput))
            {
                AssertSuccessfulEnvelope(quickFixApplyDocument.RootElement);
            }

            string fixedMissingUsing = await File.ReadAllTextAsync(
                missingUsingPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.StartsWith("using System.Text;", fixedMissingUsing, StringComparison.Ordinal);
            Assert.Contains("new StringBuilder()", fixedMissingUsing, StringComparison.Ordinal);

            (int implementationExitCode, string implementationOutput, string implementationError) =
                await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "code-action",
                        implementInterfacePath,
                        "--kind",
                        "quickfix",
                        "--line",
                        "7",
                        "--character",
                        "29",
                        "--title",
                        "Implement interface",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                implementationExitCode,
                $"{implementationError}{Environment.NewLine}{implementationOutput}");
            using (var implementationDocument = JsonDocument.Parse(implementationOutput))
            {
                JsonElement implementation = Assert.ContainsSingle(
                    implementationDocument.RootElement
                        .GetProperty("data")
                        .EnumerateArray());
                Assert.AreEqual(
                    "Implement interface",
                    implementation.GetProperty("action").GetProperty("title").GetString());
                Assert.IsNotEmpty(implementation
                    .GetProperty("editPlan")
                    .GetProperty("edit")
                    .GetProperty("documentChanges")
                    .EnumerateArray());
            }

            (int implementationApplyExitCode, string implementationApplyOutput,
                string implementationApplyError) = await RunCliAsync(
                    cliPath,
                    cliWorkerPath,
                    fixturePath,
                    [
                        "edit",
                        "code-action",
                        implementInterfacePath,
                        "--kind",
                        "quickfix",
                        "--line",
                        "7",
                        "--character",
                        "29",
                        "--title",
                        "Implement interface",
                        "--apply",
                        "--session",
                        processId,
                        "--json"
                    ],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                implementationApplyExitCode,
                $"{implementationApplyError}{Environment.NewLine}{implementationApplyOutput}");
            using (var implementationApplyDocument =
                JsonDocument.Parse(implementationApplyOutput))
            {
                AssertSuccessfulEnvelope(implementationApplyDocument.RootElement);
            }

            string implementedInterface = await File.ReadAllTextAsync(
                implementInterfacePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "public string Run(int value)",
                implementedInterface,
                StringComparison.Ordinal);
            Assert.Contains(
                "throw new NotImplementedException();",
                implementedInterface,
                StringComparison.Ordinal);

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


    /// <summary>
    /// Restores, reloads, restarts, and clears a real session while preserving its unsaved overlay.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceCommandsUseRealToolsAndPreserveOpenDocuments()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string cliPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-commands-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string projectPath = Path.Join(fixturePath, "WorkspaceCommands.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                MaintenanceDiskText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-workspace-command-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                projectPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, MaintenanceOverlayText).ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                projectPath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertOverlayDiagnosticAsync(lsp, documentPath).ConfigureAwait(false);

            JsonElement reload = await RunWorkspaceOperationAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                documentPath,
                "reload").ConfigureAwait(false);
            Assert.AreEqual("reload", reload.GetProperty("operation").GetString());
            Assert.AreEqual(
                reload.GetProperty("previousGeneration").GetInt64() + 1,
                reload.GetProperty("currentGeneration").GetInt64());
            await AssertOverlayDiagnosticAsync(lsp, documentPath).ConfigureAwait(false);

            JsonElement restore = await RunWorkspaceOperationAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                documentPath,
                "restore").ConfigureAwait(false);
            Assert.AreEqual(1, restore.GetProperty("restoredEntryPointCount").GetInt32());
            Assert.AreEqual(
                restore.GetProperty("previousGeneration").GetInt64() + 1,
                restore.GetProperty("currentGeneration").GetInt64());
            await AssertOverlayDiagnosticAsync(lsp, documentPath).ConfigureAwait(false);

            JsonElement restart = await RunWorkspaceOperationAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                documentPath,
                "restart-build-host").ConfigureAwait(false);
            Assert.IsGreaterThanOrEqualTo(
                1,
                restart.GetProperty("restartedBuildHostCount").GetInt32());
            Assert.AreEqual(
                restart.GetProperty("previousGeneration").GetInt64() + 1,
                restart.GetProperty("currentGeneration").GetInt64());
            await AssertOverlayDiagnosticAsync(lsp, documentPath).ConfigureAwait(false);

            JsonElement clear = await RunWorkspaceOperationAsync(
                cliPath,
                cliWorkerPath,
                fixturePath,
                documentPath,
                "clear-cache").ConfigureAwait(false);
            Assert.AreEqual(
                clear.GetProperty("previousGeneration").GetInt64(),
                clear.GetProperty("currentGeneration").GetInt64());
            Assert.IsGreaterThanOrEqualTo(
                1,
                clear.GetProperty("clearedCacheEntryCount").GetInt32());
            await AssertOverlayDiagnosticAsync(lsp, documentPath).ConfigureAwait(false);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task<JsonElement> RunWorkspaceOperationAsync(
        string cliPath,
        string cliWorkerPath,
        string workingDirectory,
        string workspacePath,
        string operation)
    {
        (int exitCode, string output, string error) = await RunCliAsync(
            cliPath,
            cliWorkerPath,
            workingDirectory,
            ["workspace", operation, "--workspace", workspacePath, "--json"],
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, exitCode, $"{error}{Environment.NewLine}{output}");
        using var document = JsonDocument.Parse(output);
        AssertSuccessfulEnvelope(document.RootElement);
        return document.RootElement.GetProperty("data").Clone();
    }

    private async Task AssertOverlayDiagnosticAsync(
        LspProcessSession lsp,
        string documentPath)
    {
        DocumentDiagnosticReport report = await lsp.RequestDiagnosticsAsync(
            documentPath,
            previousResultId: null,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.Contains(
            "CS0103",
            (report.Items ?? []).Select(static diagnostic => diagnostic.Code));
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

    private static Process StartCliProcess(
        string cliPath,
        string cliWorkerPath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
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
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls CLI process did not start.");
    }

    private static async Task<JsonElement> ReadWatchEventAsync(
        StreamReader standardOutput,
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            string? line = await standardOutput
                .ReadLineAsync(timeoutSource.Token)
                .ConfigureAwait(false) ?? throw new EndOfStreamException(
                    "The csls session watch stream closed before the expected event.");
            using var document = JsonDocument.Parse(line);
            AssertSuccessfulEnvelope(document.RootElement);
            JsonElement data = document.RootElement.GetProperty("data");
            if (predicate(data))
            {
                return data.Clone();
            }
        }
    }

    private static bool IsWatchEvent(JsonElement data, string kind, int processId) =>
        data.GetProperty("kind").GetString() == kind &&
        data.GetProperty("session").GetProperty("processId").GetInt32() == processId;

    private static async Task AcceptAndDisconnectAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        using var listener = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
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

    private const string MaintenanceDiskText = """
        Console.WriteLine("disk");
        """;

    private const string MaintenanceOverlayText = """
        Console.WriteLine(missingOverlay);
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

    private const string MissingUsingText = """
        namespace Fixture;

        public static class MissingUsing
        {
            public static string Build()
            {
                var builder = new StringBuilder();
                return builder.ToString();
            }
        }
        """;

    private const string ImplementInterfaceText = """
        namespace InterfaceActions;

        public interface IRunner
        {
            string Run(int value);
        }

        public sealed class Runner : IRunner
        {
        }
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
