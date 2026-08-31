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
        "Installs and verifies the latest stable upstream csharp-ls parity oracle.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-CsharpLsOracle.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-CsharpLsOracle.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    string installationPath = Path.Join(
        toolsRoot,
        "csharp-ls-oracle",
        "current",
        GetPlatform());
    Directory.CreateDirectory(installationPath);
    string executablePath = Path.Join(
        installationPath,
        OperatingSystem.IsWindows() ? "csharp-ls.exe" : "csharp-ls");
    string command = File.Exists(executablePath) ? "update" : "install";
    var startInfo = new ProcessStartInfo
    {
        FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    foreach (string argument in new[]
    {
        "tool",
        command,
        "csharp-ls",
        "--tool-path",
        installationPath
    })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("The .NET tool installer did not start.");
    Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
    Task<string> standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await standardOutput.ConfigureAwait(false) +
        await standardError.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"csharp-ls installation failed with exit code {process.ExitCode}: {output.Trim()}");
    }

    await ScriptSupport.VerifyToolAsync(
        executablePath,
        ["--version"],
        ".",
        CancellationToken.None).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
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
    string operatingSystem = OperatingSystem.IsLinux()
        ? "linux"
        : OperatingSystem.IsMacOS()
            ? "osx"
            : OperatingSystem.IsWindows()
                ? "win"
                : throw new PlatformNotSupportedException(
                    $"The upstream oracle does not support {RuntimeInformation.OSDescription}.");
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"The upstream oracle does not support {RuntimeInformation.OSArchitecture}.")
    };
    return $"{operatingSystem}-{architecture}";
}
