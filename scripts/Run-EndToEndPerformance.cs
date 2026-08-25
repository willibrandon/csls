#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

var valueOptions = new HashSet<string>(StringComparer.Ordinal)
{
    "--iterations",
    "--workspace",
    "--output",
    "--timeout-seconds",
    "--startup-budget-ms",
    "--workspace-budget-ms",
    "--ready-budget-ms",
    "--operation-budget-ms",
    "--working-set-budget-mib",
    "--private-memory-budget-mib",
    "--process-count-budget"
};
var suppliedArguments = new Dictionary<string, string>(StringComparer.Ordinal);
for (int index = 0; index < args.Length; index++)
{
    string argument = args[index];
    if (argument is "--help" or "-h" or "-?")
    {
        await Console.Out.WriteLineAsync(
            "Publishes csls and csls-mcp with Native AOT and measures real product operations.")
            .ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            "Usage: dotnet run --file scripts/Run-EndToEndPerformance.cs -- " +
            "[--iterations <count>] [--workspace <path>] [--output <path>] " +
            "[performance budget options]").ConfigureAwait(false);
        return 0;
    }

    if (!valueOptions.Contains(argument) || index + 1 >= args.Length)
    {
        await Console.Error.WriteLineAsync($"Unknown or incomplete argument: {argument}")
            .ConfigureAwait(false);
        return 2;
    }

    suppliedArguments[argument] = args[++index];
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string runtimeIdentifier = GetRuntimeIdentifier();
    string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    string artifactDirectory = Path.Join(
        repositoryRoot,
        "artifacts",
        "end-to-end-performance");
    string binlogDirectory = Path.Join(artifactDirectory, "binlogs");
    Directory.CreateDirectory(binlogDirectory);
    string serverProject = Path.Join(repositoryRoot, "src", "Csls.App", "Csls.App.csproj");
    string mcpServerProject = Path.Join(
        repositoryRoot,
        "src",
        "Csls.Mcp",
        "Csls.Mcp.csproj");
    string harnessProject = Path.Join(
        repositoryRoot,
        "benchmarks",
        "Csls.EndToEndPerformance",
        "Csls.EndToEndPerformance.csproj");
    await RunCheckedAsync(
        dotnetPath,
        [
            "publish",
            serverProject,
            "--configuration",
            "Release",
            "--runtime",
            runtimeIdentifier,
            "--self-contained",
            "true",
            $"--binaryLogger:{Path.Join(binlogDirectory, "publish-csls.binlog")}" 
        ],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        dotnetPath,
        [
            "publish",
            mcpServerProject,
            "--configuration",
            "Release",
            "--runtime",
            runtimeIdentifier,
            "--self-contained",
            "true",
            $"--binaryLogger:{Path.Join(binlogDirectory, "publish-csls-mcp.binlog")}"
        ],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        dotnetPath,
        [
            "build",
            harnessProject,
            "--configuration",
            "Release",
            $"--binaryLogger:{Path.Join(binlogDirectory, "build-harness.binlog")}" 
        ],
        repositoryRoot).ConfigureAwait(false);

    string executableExtension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
    string serverPath = Path.Join(
        repositoryRoot,
        "artifacts",
        "publish",
        "Csls.App",
        $"release_{runtimeIdentifier}",
        "csls" + executableExtension);
    string harnessPath = Path.Join(
        repositoryRoot,
        "artifacts",
        "bin",
        "Csls.EndToEndPerformance",
        "release",
        "Csls.EndToEndPerformance.dll");
    string mcpServerPath = Path.Join(
        repositoryRoot,
        "artifacts",
        "publish",
        "Csls.Mcp",
        $"release_{runtimeIdentifier}",
        "csls-mcp" + executableExtension);
    if (!File.Exists(serverPath) ||
        !File.Exists(mcpServerPath) ||
        !File.Exists(harnessPath))
    {
        throw new FileNotFoundException(
            "The end-to-end performance build did not produce its expected executables.");
    }

    string workspacePath = suppliedArguments.TryGetValue("--workspace", out string? workspace)
        ? Path.GetFullPath(workspace)
        : repositoryRoot;
    string outputPath = suppliedArguments.TryGetValue("--output", out string? output)
        ? Path.GetFullPath(output)
        : Path.Join(artifactDirectory, "results.json");
    var harnessArguments = new List<string>
    {
        harnessPath,
        serverPath,
        mcpServerPath,
        workspacePath,
        "--output",
        outputPath
    };
    foreach ((string name, string value) in suppliedArguments)
    {
        if (name is "--workspace" or "--output")
        {
            continue;
        }

        harnessArguments.Add(name);
        harnessArguments.Add(value);
    }

    return await RunAsync(dotnetPath, harnessArguments, repositoryRoot).ConfigureAwait(false);
}
catch (Exception exception) when (exception is
    IOException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static string GetRuntimeIdentifier()
{
    string operatingSystem = OperatingSystem.IsWindows()
        ? "win"
        : OperatingSystem.IsMacOS()
            ? "osx"
            : OperatingSystem.IsLinux()
                ? RuntimeInformation.RuntimeIdentifier.Contains(
                    "musl",
                    StringComparison.Ordinal)
                    ? "linux-musl"
                    : "linux"
                : throw new PlatformNotSupportedException(
                    "Native AOT performance measurements require Windows, Linux, or macOS.");
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 when OperatingSystem.IsWindows() => "x86",
        _ => throw new PlatformNotSupportedException(
            $"Unsupported performance measurement architecture: " +
            $"{RuntimeInformation.OSArchitecture}")
    };
    return $"{operatingSystem}-{architecture}";
}

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    int exitCode = await RunAsync(executablePath, arguments, workingDirectory)
        .ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {exitCode.ToString(CultureInfo.InvariantCulture)}.");
    }
}

static async Task<int> RunAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task outputTask = CopyAsync(process.StandardOutput, Console.Out);
    Task errorTask = CopyAsync(process.StandardError, Console.Error);
    await Task.WhenAll(
        process.WaitForExitAsync(),
        outputTask,
        errorTask).ConfigureAwait(false);
    return process.ExitCode;
}

static async Task CopyAsync(StreamReader source, TextWriter destination)
{
    char[] buffer = new char[4_096];
    while (true)
    {
        int read = await source.ReadAsync(buffer).ConfigureAwait(false);
        if (read == 0)
        {
            return;
        }

        await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        await destination.FlushAsync().ConfigureAwait(false);
    }
}
