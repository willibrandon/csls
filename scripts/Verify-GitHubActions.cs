#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Validates every csls GitHub Actions workflow with the provisioned actionlint binary.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-GitHubActions.cs").ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-GitHubActions.cs").ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(repositoryRoot);
    string platform = GetPlatform();
    string executableName = OperatingSystem.IsWindows() ? "actionlint.exe" : "actionlint";
    string toolRoot = Path.Join(toolsRoot, "actionlint");
    string executablePath = Directory.Exists(toolRoot)
        ? Directory
            .EnumerateDirectories(toolRoot)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(versionPath => Path.Join(versionPath, platform))
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(
                path,
                executableName,
                SearchOption.AllDirectories))
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                executableName,
                StringComparison.Ordinal))
            ?? string.Empty
        : string.Empty;
    if (!File.Exists(executablePath))
    {
        throw new FileNotFoundException(
            "actionlint is not provisioned. Run scripts/Provision-Actionlint.cs.");
    }

    string workflowPath = Path.Join(repositoryRoot, ".github", "workflows");
    string[] workflows =
    [
        .. Directory
            .EnumerateFiles(workflowPath, "*.yml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(workflowPath, "*.yaml", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.Ordinal)
    ];
    if (workflows.Length == 0)
    {
        throw new InvalidDataException("No GitHub Actions workflows were found.");
    }

    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = repositoryRoot
    };
    foreach (string workflow in workflows)
    {
        startInfo.ArgumentList.Add(workflow);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("actionlint did not start.");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidDataException(
            $"actionlint failed with exit code {process.ExitCode}:" +
            $"{Environment.NewLine}{standardOutput}{standardError}");
    }

    await Console.Out.WriteLineAsync(
        $"Validated {workflows.Length} GitHub Actions workflows with actionlint.")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    PlatformNotSupportedException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static string GetPlatform()
{
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"actionlint does not support {RuntimeInformation.OSArchitecture}.")
    };
    if (OperatingSystem.IsWindows())
    {
        return $"win-{architecture}";
    }

    if (OperatingSystem.IsMacOS())
    {
        return $"osx-{architecture}";
    }

    if (OperatingSystem.IsLinux())
    {
        return $"linux-{architecture}";
    }

    throw new PlatformNotSupportedException(
        $"actionlint does not support {RuntimeInformation.OSDescription}.");
}
