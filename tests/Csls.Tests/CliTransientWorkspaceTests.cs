using Csls.Control;
using Csls.Control.Contracts;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies agent-oriented CLI commands against real transient language-server workspaces.
/// </summary>
[TestClass]
public sealed class CliTransientWorkspaceTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Starts and cleans a transient server for query and edit commands without a live session.
    /// </summary>
    [TestMethod]
    public async Task QueryAndEditWithWorkspaceUseTransientLanguageServer()
    {
        string fixturePath = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            (string cliPath, string cliWorkerPath, string serverWorkerPath) = ResolveTools();
            (int queryExitCode, string queryOutput, string queryError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                serverWorkerPath,
                fixturePath,
                ["query", "symbols", "Widget", "--workspace", fixturePath, "--json"])
                .ConfigureAwait(false);

            Assert.AreEqual(
                0,
                queryExitCode,
                $"{queryError}{Environment.NewLine}{queryOutput}");
            using (var queryDocument = JsonDocument.Parse(queryOutput))
            {
                JsonElement root = AssertSuccessfulEnvelope(queryDocument.RootElement);
                Assert.HasCount(2, root.GetProperty("data").EnumerateArray());
            }

            string documentPath = Path.Join(fixturePath, "Widgets.cs");
            (int editExitCode, string editOutput, string editError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                serverWorkerPath,
                fixturePath,
                ["edit", "format", documentPath, "--workspace", fixturePath, "--json"])
                .ConfigureAwait(false);

            Assert.AreEqual(
                0,
                editExitCode,
                $"{editError}{Environment.NewLine}{editOutput}");
            using (var editDocument = JsonDocument.Parse(editOutput))
            {
                JsonElement root = AssertSuccessfulEnvelope(editDocument.RootElement);
                JsonElement data = root.GetProperty("data");
                Assert.AreNotEqual(Guid.Empty, data.GetProperty("planId").GetGuid());
                Assert.IsNotEmpty(
                    data.GetProperty("edit").GetProperty("documentChanges").EnumerateArray());
            }

            IReadOnlyList<ControlSessionInfo> sessions = await ControlSessionDiscovery
                .DiscoverAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsEmpty(
                sessions.Where(session => session.WorkspaceRoots.Any(
                    root => string.Equals(root, fixturePath, StringComparison.Ordinal))));
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Continues an opaque cursor across independent transient CLI invocations.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceSymbolCursorContinuesAcrossInvocations()
    {
        string fixturePath = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            (string cliPath, string cliWorkerPath, string serverWorkerPath) = ResolveTools();
            (int firstExitCode, string firstOutput, string firstError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                serverWorkerPath,
                fixturePath,
                [
                    "query",
                    "symbols",
                    "Widget",
                    "--workspace",
                    fixturePath,
                    "--limit",
                    "1",
                    "--json"
                ]).ConfigureAwait(false);

            Assert.AreEqual(
                0,
                firstExitCode,
                $"{firstError}{Environment.NewLine}{firstOutput}");
            string firstName;
            string cursor;
            using (var firstDocument = JsonDocument.Parse(firstOutput))
            {
                JsonElement root = AssertSuccessfulEnvelope(firstDocument.RootElement, hasNext: true);
                JsonElement data = root.GetProperty("data");
                Assert.AreEqual(1, data.GetArrayLength());
                firstName = data[0].GetProperty("name").GetString()!;
                cursor = root.GetProperty("nextCursor").GetString()!;
            }

            (int secondExitCode, string secondOutput, string secondError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                serverWorkerPath,
                fixturePath,
                [
                    "query",
                    "symbols",
                    "Widget",
                    "--workspace",
                    fixturePath,
                    "--cursor",
                    cursor,
                    "--limit",
                    "1",
                    "--json"
                ]).ConfigureAwait(false);

            Assert.AreEqual(
                0,
                secondExitCode,
                $"{secondError}{Environment.NewLine}{secondOutput}");
            using var secondDocument = JsonDocument.Parse(secondOutput);
            JsonElement secondRoot = AssertSuccessfulEnvelope(secondDocument.RootElement);
            JsonElement secondData = secondRoot.GetProperty("data");
            Assert.AreEqual(1, secondData.GetArrayLength());
            Assert.AreNotEqual(firstName, secondData[0].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Rejects a cursor when it is replayed against a different operation.
    /// </summary>
    [TestMethod]
    public async Task CursorIsBoundToItsOperation()
    {
        string fixturePath = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            (string cliPath, string cliWorkerPath, string serverWorkerPath) = ResolveTools();
            (int firstExitCode, string firstOutput, string firstError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                serverWorkerPath,
                fixturePath,
                [
                    "query",
                    "symbols",
                    "Widget",
                    "--workspace",
                    fixturePath,
                    "--limit",
                    "1",
                    "--json"
                ]).ConfigureAwait(false);

            Assert.AreEqual(
                0,
                firstExitCode,
                $"{firstError}{Environment.NewLine}{firstOutput}");
            string cursor;
            using (var firstDocument = JsonDocument.Parse(firstOutput))
            {
                cursor = firstDocument.RootElement.GetProperty("nextCursor").GetString()!;
            }

            string documentPath = Path.Join(fixturePath, "Widgets.cs");
            (int invalidExitCode, string invalidOutput, string invalidError) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                serverWorkerPath,
                fixturePath,
                [
                    "query",
                    "document-symbols",
                    documentPath,
                    "--workspace",
                    fixturePath,
                    "--cursor",
                    cursor,
                    "--json"
                ]).ConfigureAwait(false);

            Assert.AreEqual(1, invalidExitCode, invalidError);
            using var invalidDocument = JsonDocument.Parse(invalidOutput);
            JsonElement root = invalidDocument.RootElement;
            Assert.IsFalse(root.GetProperty("success").GetBoolean());
            Assert.AreEqual(
                "operation-failed",
                root.GetProperty("data").GetProperty("code").GetString());
            Assert.Contains(
                "invalid for this operation",
                root.GetProperty("data").GetProperty("message").GetString()!,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Expands a System.CommandLine response file for a real transient query.
    /// </summary>
    [TestMethod]
    public async Task ResponseFileRunsTransientQuery()
    {
        string fixturePath = await CreateFixtureAsync().ConfigureAwait(false);
        try
        {
            (string cliPath, string cliWorkerPath, string serverWorkerPath) = ResolveTools();
            string responsePath = Path.Join(fixturePath, "query.rsp");
            await File.WriteAllLinesAsync(
                responsePath,
                ["query", "symbols", "Widget", "--workspace", fixturePath, "--json"],
                TestContext.CancellationToken).ConfigureAwait(false);

            (int exitCode, string output, string error) = await RunCliAsync(
                cliPath,
                cliWorkerPath,
                serverWorkerPath,
                fixturePath,
                [$"@{responsePath}"]).ConfigureAwait(false);

            Assert.AreEqual(0, exitCode, $"{error}{Environment.NewLine}{output}");
            using var document = JsonDocument.Parse(output);
            JsonElement root = AssertSuccessfulEnvelope(document.RootElement);
            Assert.HasCount(2, root.GetProperty("data").EnumerateArray());
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Returns command suggestions through the System.CommandLine directive protocol.
    /// </summary>
    [TestMethod]
    public async Task SuggestDirectiveReturnsMatchingCommands()
    {
        (string cliPath, string cliWorkerPath, string serverWorkerPath) = ResolveTools();
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        (int exitCode, string output, string error) = await RunCliAsync(
            cliPath,
            cliWorkerPath,
            serverWorkerPath,
            repositoryRoot,
            ["[suggest:1]", "qu"]).ConfigureAwait(false);

        Assert.AreEqual(0, exitCode, error);
        string[] suggestions = output.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.HasCount(2, suggestions);
        Assert.Contains("query", suggestions);
        Assert.Contains("requests", suggestions);
    }

    private static JsonElement AssertSuccessfulEnvelope(JsonElement root, bool hasNext = false)
    {
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.IsTrue(Guid.TryParse(root.GetProperty("correlationId").GetString(), out _));
        Assert.IsTrue(root.GetProperty("success").GetBoolean());
        if (hasNext)
        {
            Assert.IsNotNull(root.GetProperty("nextCursor").GetString());
        }
        else
        {
            Assert.AreEqual(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
        }

        return root;
    }

    private static async Task<string> CreateFixtureAsync()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-cli-transient-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "TransientFixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Widgets.cs"),
            DocumentText).ConfigureAwait(false);
        return fixturePath;
    }

    private static (string CliPath, string CliWorkerPath, string ServerWorkerPath) ResolveTools()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string cliPath = Path.Join(artifactsRoot, "bin", "Csls.App", "debug", "csls.dll");
        string cliWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Cli.Worker",
            "debug",
            "csls-cli-worker.dll");
        string serverWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(cliPath), $"CLI launcher not found at {cliPath}.");
        Assert.IsTrue(File.Exists(cliWorkerPath), $"CLI worker not found at {cliWorkerPath}.");
        Assert.IsTrue(File.Exists(serverWorkerPath), $"Server worker not found at {serverWorkerPath}.");
        return (cliPath, cliWorkerPath, serverWorkerPath);
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(
        string cliPath,
        string cliWorkerPath,
        string serverWorkerPath,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add(cliPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CSLS_CLI_WORKER_PATH"] = cliWorkerPath;
        startInfo.Environment["CSLS_SERVER_WORKER_PATH"] = serverWorkerPath;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls CLI process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        return (
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
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
        namespace TransientFixture;

        public sealed class AlphaWidget{ }

        public sealed class BetaWidget{ }
        """;
}
