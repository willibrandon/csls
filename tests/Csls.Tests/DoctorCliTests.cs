using Csls.Control;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies workspace doctor behavior through the real public CLI and language server.
/// </summary>
[TestClass]
public sealed class DoctorCliTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Loads a real project, inspects Roslyn state, and writes a real MSBuild binary log.
    /// </summary>
    [TestMethod]
    public async Task DoctorLoadsWorkspaceAndWritesBinaryLog()
    {
        string fixturePath = CreateFixturePath();
        Directory.CreateDirectory(fixturePath);
        try
        {
            await WriteProjectAsync(
                fixturePath,
                "Console.WriteLine(\"doctor\");").ConfigureAwait(false);
            string binlogPath = Path.Join(fixturePath, "doctor.binlog");

            (int exitCode, string output, string error) = await RunDoctorAsync(
                fixturePath,
                ["doctor", fixturePath, "--binlog", binlogPath, "--json"])
                .ConfigureAwait(false);

            Assert.AreEqual(0, exitCode, $"{error}{Environment.NewLine}{output}");
            using var document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            AssertSuccessfulEnvelope(root);
            JsonElement data = root.GetProperty("data");
            Assert.AreEqual(fixturePath, data.GetProperty("workspacePath").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(data.GetProperty("sdkVersion").GetString()));
            Assert.AreEqual(1, data.GetProperty("workspaces").GetArrayLength());
            Assert.AreEqual(1, data.GetProperty("projects").GetArrayLength());
            Assert.AreEqual(
                "DoctorFixture",
                data.GetProperty("projects")[0].GetProperty("name").GetString());
            Assert.IsGreaterThan(0, data.GetProperty("documentCount").GetInt32());
            Assert.IsGreaterThan(0, data.GetProperty("buildHosts").GetArrayLength());
            Assert.IsGreaterThan(0, data.GetProperty("logs").GetArrayLength());
            Assert.DoesNotContain(
                "Fail",
                data.GetProperty("checks")
                    .EnumerateArray()
                    .Select(static check => check.GetProperty("status").GetString()));
            Assert.Contains(
                "msbuild-binlog",
                data.GetProperty("checks")
                    .EnumerateArray()
                    .Select(static check => check.GetProperty("name").GetString()));
            Assert.IsTrue(File.Exists(binlogPath));
            Assert.IsGreaterThan(0L, new FileInfo(binlogPath).Length);

            int processId = data.GetProperty("buildHosts")[0]
                .GetProperty("processId")
                .GetInt32();
            Assert.IsFalse(File.Exists(ControlEndpoint.GetSocketPath(processId)));
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports real compiler errors without treating valid server startup as unhealthy.
    /// </summary>
    [TestMethod]
    public async Task DoctorReportsSourceErrorsAsWarnings()
    {
        string fixturePath = CreateFixturePath();
        Directory.CreateDirectory(fixturePath);
        try
        {
            await WriteProjectAsync(
                fixturePath,
                "Console.WriteLine(MissingSymbol);").ConfigureAwait(false);

            (int exitCode, string output, string error) = await RunDoctorAsync(
                fixturePath,
                ["doctor", "--json"]).ConfigureAwait(false);

            Assert.AreEqual(0, exitCode, $"{error}{Environment.NewLine}{output}");
            using var document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            AssertSuccessfulEnvelope(root);
            JsonElement data = root.GetProperty("data");
            Assert.Contains(
                "CS0103",
                data.GetProperty("diagnostics")
                    .EnumerateArray()
                    .Select(static diagnostic => diagnostic.GetProperty("id").GetString()));
            JsonElement diagnosticCheck = data.GetProperty("checks")
                .EnumerateArray()
                .Single(static check =>
                    check.GetProperty("name").GetString() == "source-diagnostics");
            Assert.AreEqual("Warning", diagnosticCheck.GetProperty("status").GetString());
            Assert.IsTrue(data.GetProperty("isHealthy").GetBoolean());
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns a structured failing report when the requested workspace does not exist.
    /// </summary>
    [TestMethod]
    public async Task DoctorRejectsMissingWorkspace()
    {
        string missingPath = Path.Join(
            Path.GetTempPath(),
            $"csls-doctor-missing-{Guid.NewGuid():N}");

        (int exitCode, string output, string error) = await RunDoctorAsync(
            Environment.CurrentDirectory,
            ["doctor", missingPath, "--json"]).ConfigureAwait(false);

        Assert.AreEqual(1, exitCode, error);
        using var document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.IsFalse(root.GetProperty("success").GetBoolean());
        JsonElement data = root.GetProperty("data");
        Assert.IsFalse(data.GetProperty("isHealthy").GetBoolean());
        JsonElement targetCheck = data.GetProperty("checks")[0];
        Assert.AreEqual("workspace-target", targetCheck.GetProperty("name").GetString());
        Assert.AreEqual("Fail", targetCheck.GetProperty("status").GetString());
    }

    /// <summary>
    /// Returns a structured failure when global.json selects an unavailable SDK.
    /// </summary>
    [TestMethod]
    public async Task DoctorReportsUnavailableSdk()
    {
        string fixturePath = CreateFixturePath();
        Directory.CreateDirectory(fixturePath);
        try
        {
            await WriteProjectAsync(
                fixturePath,
                "Console.WriteLine(\"sdk\");").ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "global.json"),
                UnavailableSdkText,
                TestContext.CancellationToken).ConfigureAwait(false);

            (int exitCode, string output, string error) = await RunDoctorAsync(
                fixturePath,
                ["doctor", fixturePath, "--json"]).ConfigureAwait(false);

            Assert.AreEqual(1, exitCode, error);
            using var document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            Assert.IsFalse(root.GetProperty("success").GetBoolean());
            JsonElement data = root.GetProperty("data");
            JsonElement sdkCheck = data.GetProperty("checks")
                .EnumerateArray()
                .Single(static check => check.GetProperty("name").GetString() == "dotnet-sdk");
            Assert.AreEqual("Fail", sdkCheck.GetProperty("status").GetString());
            Assert.AreEqual(0, data.GetProperty("projects").GetArrayLength());
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task WriteProjectAsync(string fixturePath, string source)
    {
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "DoctorFixture.csproj"),
            ProjectText,
            TestContext.CancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Program.cs"),
            source,
            TestContext.CancellationToken).ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunDoctorAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string cliPath = Environment.GetEnvironmentVariable("CSLS_TEST_CLI_PATH") ?? Path.Join(
            artifactsRoot,
            "bin",
            "Csls.App",
            "debug",
            "csls.dll");
        string cliWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_CLI_WORKER_PATH") ?? Path.Join(
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

        var startInfo = new ProcessStartInfo
        {
            FileName = string.Equals(
                Path.GetExtension(cliPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase)
                ? EditorToolResolver.ResolveDotNetHost()
                : cliPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        if (string.Equals(
            Path.GetExtension(cliPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(cliPath);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CSLS_CLI_WORKER_PATH"] = cliWorkerPath;
        startInfo.Environment["CSLS_SERVER_WORKER_PATH"] = serverWorkerPath;
        startInfo.Environment["DOTNET_HOST_PATH"] = EditorToolResolver.ResolveDotNetHost();
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The csls doctor process did not start.");
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

    private static string CreateFixturePath() => Path.Join(
        Path.GetTempPath(),
        $"csls-doctor-{Guid.NewGuid():N}");

    private static void AssertSuccessfulEnvelope(JsonElement root)
    {
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.IsTrue(root.GetProperty("success").GetBoolean());
        Assert.IsTrue(Guid.TryParse(root.GetProperty("correlationId").GetString(), out _));
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string UnavailableSdkText = """
        {
          "sdk": {
            "version": "99.0.100",
            "rollForward": "disable"
          }
        }
        """;
}
