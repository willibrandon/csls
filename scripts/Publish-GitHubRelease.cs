#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Creates or updates the tagged GitHub release with verified public assets.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-GitHubRelease.cs -- " +
        "--tag <tag> --version <version> --assets <path> --prerelease <true|false>")
        .ConfigureAwait(false);
    return 0;
}

string? tag = null;
string? version = null;
string? assetsPath = null;
bool? prerelease = null;
for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex += 2)
{
    if (argumentIndex + 1 >= args.Length)
    {
        return await WriteUsageErrorAsync().ConfigureAwait(false);
    }

    string value = args[argumentIndex + 1];
    switch (args[argumentIndex])
    {
        case "--tag":
            tag = value;
            break;
        case "--version":
            version = value;
            break;
        case "--assets":
            assetsPath = Path.GetFullPath(value);
            break;
        case "--prerelease" when bool.TryParse(value, out bool parsedPrerelease):
            prerelease = parsedPrerelease;
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

if (tag is null || version is null || assetsPath is null || prerelease is null)
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

try
{
    string[] assets =
    [
        .. Directory.EnumerateFiles(assetsPath, "*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
    ];
    if (assets.Length == 0)
    {
        throw new InvalidDataException("No GitHub release assets were found.");
    }

    (int viewExitCode, _, _) = await RunAsync(
        "gh",
        ["release", "view", tag, "--json", "url"]).ConfigureAwait(false);
    if (viewExitCode != 0)
    {
        List<string> createArguments =
        [
            "release",
            "create",
            tag,
            "--verify-tag",
            "--generate-notes",
            "--title",
            $"csls {version}"
        ];
        if (prerelease.Value)
        {
            createArguments.Add("--prerelease");
        }
        else
        {
            createArguments.Add("--latest");
        }

        await RunCheckedAsync("gh", createArguments).ConfigureAwait(false);
    }

    List<string> uploadArguments = ["release", "upload", tag, "--clobber"];
    uploadArguments.AddRange(assets);
    await RunCheckedAsync("gh", uploadArguments).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        $"Published {assets.Length} assets to GitHub release {tag}.")
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

static async Task<int> WriteUsageErrorAsync()
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-GitHubRelease.cs -- " +
        "--tag <tag> --version <version> --assets <path> --prerelease <true|false>")
        .ConfigureAwait(false);
    return 2;
}

static async Task RunCheckedAsync(string executablePath, IReadOnlyList<string> arguments)
{
    (int exitCode, string output, string error) = await RunAsync(
        executablePath,
        arguments).ConfigureAwait(false);
    await Console.Out.WriteAsync(output).ConfigureAwait(false);
    await Console.Error.WriteAsync(error).ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {exitCode}.");
    }
}

static async Task<(int ExitCode, string Output, string Error)> RunAsync(
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
        ?? throw new InvalidOperationException($"{executablePath} did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    return (
        process.ExitCode,
        await outputTask.ConfigureAwait(false),
        await errorTask.ConfigureAwait(false));
}
