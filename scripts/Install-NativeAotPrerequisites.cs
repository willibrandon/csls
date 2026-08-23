#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs platform build prerequisites for one csls Native AOT runtime identifier.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Install-NativeAotPrerequisites.cs -- " +
        "--runtime <rid>").ConfigureAwait(false);
    return 0;
}

if (args.Length != 2 ||
    !string.Equals(args[0], "--runtime", StringComparison.Ordinal) ||
    string.IsNullOrWhiteSpace(args[1]))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Install-NativeAotPrerequisites.cs -- " +
        "--runtime <rid>").ConfigureAwait(false);
    return 2;
}

try
{
    string runtimeIdentifier = args[1];
    if (!OperatingSystem.IsLinux() || !File.Exists("/etc/debian_version"))
    {
        await Console.Out.WriteLineAsync(
            $"{runtimeIdentifier} uses the build tools supplied by this runner image.")
            .ConfigureAwait(false);
        return 0;
    }

    string packageManager = string.Equals(
        Environment.UserName,
        "root",
        StringComparison.Ordinal)
        ? "apt-get"
        : "sudo";
    await RunPackageManagerAsync(
        packageManager,
        string.Equals(packageManager, "sudo", StringComparison.Ordinal)
            ? ["apt-get", "update"]
            : ["update"]).ConfigureAwait(false);
    List<string> packages =
    [
        "build-essential",
        "clang",
        "libncurses-dev",
        "zlib1g-dev"
    ];
    if (runtimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal))
    {
        packages.Add("musl-tools");
    }

    List<string> installArguments = string.Equals(
        packageManager,
        "sudo",
        StringComparison.Ordinal)
        ? ["apt-get", "install", "--yes", "--no-install-recommends", .. packages]
        : ["install", "--yes", "--no-install-recommends", .. packages];
    await RunPackageManagerAsync(packageManager, installArguments).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        $"Installed Native AOT prerequisites for {runtimeIdentifier}.").ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task RunPackageManagerAsync(
    string executablePath,
    IReadOnlyList<string> arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
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
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    await Console.Out.WriteAsync(standardOutput).ConfigureAwait(false);
    await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}.");
    }
}
