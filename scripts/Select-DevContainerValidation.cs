#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Selects full or repository-only development container validation from changed files.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Select-DevContainerValidation.cs [-- --base <commit>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--base", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Select-DevContainerValidation.cs [-- --base <commit>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    bool fullValidation = args.Length == 0 || await HasRelevantChangesAsync(args[1])
        .ConfigureAwait(false);
    string output = $"full={(fullValidation ? "true" : "false")}";
    string? githubOutputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
    if (!string.IsNullOrWhiteSpace(githubOutputPath))
    {
        await File.AppendAllTextAsync(
            githubOutputPath,
            output + Environment.NewLine).ConfigureAwait(false);
    }

    await Console.Out.WriteLineAsync(output).ConfigureAwait(false);
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

static async Task<bool> HasRelevantChangesAsync(string baseCommit)
{
    if (string.IsNullOrWhiteSpace(baseCommit))
    {
        throw new InvalidDataException("The base commit cannot be empty.");
    }

    string repositoryRoot = FindRepositoryRoot();
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = repositoryRoot
    };
    foreach (string argument in new[]
    {
        "diff",
        "--name-only",
        "--diff-filter=ACDMRTUXB",
        baseCommit,
        "HEAD",
        "--"
    })
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Git did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Git failed with exit code {process.ExitCode}:{Environment.NewLine}{error}");
    }

    return output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Any(IsRelevantPath);
}

static bool IsRelevantPath(string path) =>
    path.StartsWith(".devcontainer/", StringComparison.Ordinal) ||
    path.StartsWith("scripts/", StringComparison.Ordinal) ||
    path.StartsWith("src/", StringComparison.Ordinal) ||
    path.StartsWith("tests/", StringComparison.Ordinal) ||
    path is
        ".github/workflows/ci.yml" or
        ".github/workflows/dev-container.yml" or
        "Directory.Build.props" or
        "Directory.Build.targets" or
        "Directory.Packages.props" or
        "docs-site/package.json" or
        "docs-site/package-lock.json" or
        "global.json";

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
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
