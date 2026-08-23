using System.Diagnostics;
using System.Globalization;
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
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
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
                Helper();
            }

            private static void Helper()
            {
            }
        }
        """;
}
