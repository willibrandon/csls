#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.ComponentModel;
using System.Diagnostics;

const string Version = "1.134.0";
const string NpmVersion = "12.0.2";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs the pinned VS Code extension test client and editor runtime.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-VsCode.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-VsCode.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    string fixturePath = Path.Join(repositoryRoot, "tests", "vscode");
    (string npxExecutable, IReadOnlyList<string> npxPrefix) = ResolveNpxInvocation();
    await RunCheckedAsync(
        npxExecutable,
        [
            .. npxPrefix,
            "--yes",
            $"npm@{NpmVersion}",
            "ci",
            "--ignore-scripts",
            "--prefix",
            fixturePath
        ],
        repositoryRoot).ConfigureAwait(false);

    string cachePath = Path.Join(toolsRoot, "vscode", Version);
    Directory.CreateDirectory(cachePath);
    string executablePath = (await RunCheckedAsync(
        "node",
        [Path.Join(fixturePath, "provision.mjs"), cachePath],
        repositoryRoot).ConfigureAwait(false)).Trim();
    if (!File.Exists(executablePath))
    {
        throw new InvalidDataException(
            $"The VS Code {Version} provisioner returned a missing executable: " +
            executablePath);
    }

    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException or
    Win32Exception)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static (string Executable, IReadOnlyList<string> Prefix) ResolveNpxInvocation()
{
    if (!OperatingSystem.IsWindows())
    {
        return ("npx", []);
    }

    string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    (string NodePath, string NpxCliPath) invocation = path
        .Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static directory =>
        {
            string normalizedDirectory = directory.Trim('"');
            return (
                NodePath: Path.Join(normalizedDirectory, "node.exe"),
                NpxCliPath: Path.Join(
                    normalizedDirectory,
                    "node_modules",
                    "npm",
                    "bin",
                    "npx-cli.js"));
        })
        .FirstOrDefault(static candidate =>
            File.Exists(candidate.NodePath) && File.Exists(candidate.NpxCliPath));
    if (invocation.NodePath is not null && invocation.NpxCliPath is not null)
    {
        return (invocation.NodePath, [invocation.NpxCliPath]);
    }

    throw new FileNotFoundException(
        "Node.js is installed without the npm npx CLI required to provision VS Code.");
}

static async Task<string> RunCheckedAsync(
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
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await standardOutputTask.ConfigureAwait(false);
    string error = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}: {error.Trim()}");
    }

    return output;
}
