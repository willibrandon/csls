#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Globalization;

const string usage =
    "Usage: dotnet run --file scripts/Verify-FileApps.cs " +
    "[--group-index <index> --group-count <count>]";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Compiles every repository file app and verifies its help boundary.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        usage).ConfigureAwait(false);
    return 0;
}

int groupIndex = 0;
int groupCount = 1;
if (args.Length != 0 &&
    (args.Length != 4 ||
        !string.Equals(args[0], "--group-index", StringComparison.Ordinal) ||
        !int.TryParse(
            args[1],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out groupIndex) ||
        !string.Equals(args[2], "--group-count", StringComparison.Ordinal) ||
        !int.TryParse(
            args[3],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out groupCount) ||
        groupIndex < 0 ||
        groupCount < 1 ||
        groupIndex >= groupCount))
{
    await Console.Error.WriteLineAsync(usage).ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = FindRepositoryRoot();
    string[] allFileApps =
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
    string[] fileApps =
    [
        .. allFileApps.Where((_, index) => index % groupCount == groupIndex)
    ];
    Task<(string FileApp, int ExitCode, string Output, string Error)>[] verifications =
    [
        .. fileApps.Select(async fileApp =>
        {
            (int exitCode, string output, string error) = await RunAsync(
                fileApp,
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
                !HasExpectedHelp(result.ExitCode, result.Output, result.Error))
            .Select(static result =>
                $"{Path.GetFileName(result.FileApp)} help verification failed with exit " +
                $"code {result.ExitCode}:{Environment.NewLine}" +
                $"{result.Output}{result.Error}")
    ];
    if (failures.Length > 0)
    {
        throw new InvalidDataException(string.Join(Environment.NewLine, failures));
    }

    string groupDescription = groupCount == 1
        ? string.Empty
        : $" in group {groupIndex + 1} of {groupCount}";
    await Console.Out.WriteLineAsync(
        $"Compiled and verified {fileApps.Length} repository file apps{groupDescription}.")
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
    string fileApp,
    string workingDirectory)
{
    string name = Path.GetFileName(fileApp);
    long started = Stopwatch.GetTimestamp();
    await Console.Error.WriteLineAsync(
        $"Starting {name} help verification.").ConfigureAwait(false);
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory,
        ArgumentList = { "run", "--file", fileApp, "--", "--help" }
    };
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"{name} help verification did not start.");
    await Console.Error.WriteLineAsync(
        $"Started {name} help verification (PID {process.Id}).").ConfigureAwait(false);
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    string elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds
        .ToString("F3", CultureInfo.InvariantCulture);
    bool passed = HasExpectedHelp(process.ExitCode, output, error);
    await Console.Error.WriteLineAsync(
        $"Completed {name} help verification (PID {process.Id}, exit {process.ExitCode}, " +
        $"{elapsed}s, {(passed ? "passed" : "failed")}).").ConfigureAwait(false);
    if (!passed)
    {
        await Console.Error.WriteLineAsync(
            $"{name} help verification failed:{Environment.NewLine}{output}{error}")
            .ConfigureAwait(false);
    }

    return (process.ExitCode, output, error);
}

static bool HasExpectedHelp(int exitCode, string output, string error) =>
    exitCode == 0 &&
    output.Contains("Usage:", StringComparison.Ordinal) &&
    string.IsNullOrWhiteSpace(error);
