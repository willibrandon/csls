using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Benchmarks;

/// <summary>
/// Measures complete workspace inspection through the public CLI and a transient server.
/// </summary>
[BenchmarkCategory("CLI", "Startup")]
public class WorkspaceDoctorBenchmarks
{
    private string _cliPath = null!;
    private string _cliWorkerPath = null!;
    private string _serverWorkerPath = null!;
    private string _fixturePath = null!;

    /// <summary>
    /// Creates a real SDK project and resolves the release CLI components.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-doctor-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "DoctorBenchmark.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Program.cs"),
            "Console.WriteLine(\"benchmark\");").ConfigureAwait(false);

        string repositoryRoot = FindRepositoryRoot();
        string artifactsRoot = Path.Join(repositoryRoot, "artifacts", "bin");
        _cliPath = Path.Join(artifactsRoot, "Csls.App", "release", "csls.dll");
        _cliWorkerPath = Path.Join(
            artifactsRoot,
            "Csls.Cli.Worker",
            "release",
            "csls-cli-worker.dll");
        _serverWorkerPath = Path.Join(
            artifactsRoot,
            "Csls.Worker",
            "release",
            "csls-worker.dll");
        EnsureFileExists(_cliPath);
        EnsureFileExists(_cliWorkerPath);
        EnsureFileExists(_serverWorkerPath);
    }

    /// <summary>
    /// Measures SDK selection, server startup, Roslyn loading, inspection, and shutdown.
    /// </summary>
    /// <returns>Zero after validating the successful JSON result.</returns>
    [Benchmark]
    public async Task<int> InspectWorkspaceAsync()
    {
        string dotNetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotNetHost,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = _fixturePath
        };
        startInfo.ArgumentList.Add(_cliPath);
        startInfo.ArgumentList.Add("doctor");
        startInfo.ArgumentList.Add(_fixturePath);
        startInfo.ArgumentList.Add("--json");
        startInfo.Environment["CSLS_CLI_WORKER_PATH"] = _cliWorkerPath;
        startInfo.Environment["CSLS_SERVER_WORKER_PATH"] = _serverWorkerPath;
        startInfo.Environment["DOTNET_HOST_PATH"] = dotNetHost;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The doctor benchmark process did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        if (process.ExitCode != 0 ||
            !document.RootElement.GetProperty("success").GetBoolean())
        {
            throw new InvalidOperationException(
                $"The doctor benchmark failed with exit code {process.ExitCode}: {error}");
        }

        return process.ExitCode;
    }

    /// <summary>
    /// Removes the isolated SDK project after all benchmark measurements.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_fixturePath, recursive: true);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The csls repository root was not found.");
    }

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Build the release benchmark dependencies first.", path);
        }
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;
}
