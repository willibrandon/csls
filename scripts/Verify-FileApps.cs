#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Compiles every repository file app and verifies its help boundary.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-FileApps.cs").ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-FileApps.cs").ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = FindRepositoryRoot();
    string[] fileApps =
    [
        .. Directory.EnumerateFiles(
            Path.Join(repositoryRoot, "scripts"),
            "*.cs",
            SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                "ScriptSupport.cs",
                StringComparison.Ordinal) &&
                !string.Equals(
                    Path.GetFileName(path),
                    "Verify-FileApps.cs",
                    StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
    ];
    Task<(string FileApp, int ExitCode, string Output, string Error)>[] verifications =
    [
        .. fileApps.Select(async fileApp =>
        {
            (int exitCode, string output, string error) = await RunAsync(
                "dotnet",
                ["run", "--file", fileApp, "--", "--help"],
                repositoryRoot).ConfigureAwait(false);
            return (fileApp, exitCode, output, error);
        })
    ];
    (string FileApp, int ExitCode, string Output, string Error)[] results =
        await Task.WhenAll(verifications).ConfigureAwait(false);
    string[] failures =
    [
        .. results
            .Where(static result =>
                result.ExitCode != 0 ||
                !result.Output.Contains("Usage:", StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(result.Error))
            .Select(static result =>
                $"{Path.GetFileName(result.FileApp)} help verification failed with exit " +
                $"code {result.ExitCode}:{Environment.NewLine}" +
                $"{result.Output}{result.Error}")
    ];
    if (failures.Length > 0)
    {
        throw new InvalidDataException(string.Join(Environment.NewLine, failures));
    }

    await Console.Out.WriteLineAsync(
        $"Compiled and verified {fileApps.Length} repository file apps.")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static string FindRepositoryRoot()
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

    directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}

static async Task<(int ExitCode, string Output, string Error)> RunAsync(
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
        ?? throw new InvalidOperationException($"{executablePath} did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    return (
        process.ExitCode,
        await outputTask.ConfigureAwait(false),
        await errorTask.ConfigureAwait(false));
}
