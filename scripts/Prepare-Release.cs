#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package NuGet.Versioning

using NuGet.Versioning;
using System.Diagnostics;
using System.Globalization;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Validates a release version and writes canonical GitHub Actions outputs.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Prepare-Release.cs -- " +
        "--version <version-or-vtag> --ref <git-ref> --event <event> " +
        "--dry-run <true|false> [--output <path>]")
        .ConfigureAwait(false);
    return 0;
}

string? suppliedVersion = null;
string? gitReference = null;
string? eventName = null;
string? outputPath = null;
bool? dryRun = null;
for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex += 2)
{
    if (argumentIndex + 1 >= args.Length)
    {
        return await WriteUsageErrorAsync().ConfigureAwait(false);
    }

    string value = args[argumentIndex + 1];
    switch (args[argumentIndex])
    {
        case "--version":
            suppliedVersion = value;
            break;
        case "--ref":
            gitReference = value;
            break;
        case "--event":
            eventName = value;
            break;
        case "--dry-run" when bool.TryParse(value, out bool parsedDryRun):
            dryRun = parsedDryRun;
            break;
        case "--output":
            outputPath = Path.GetFullPath(value);
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

if (string.IsNullOrWhiteSpace(suppliedVersion) ||
    string.IsNullOrWhiteSpace(gitReference) ||
    string.IsNullOrWhiteSpace(eventName) ||
    dryRun is null)
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

string versionText = suppliedVersion.StartsWith('v')
    ? suppliedVersion[1..]
    : suppliedVersion;
if (!NuGetVersion.TryParse(versionText, out NuGetVersion? version) ||
    !string.Equals(versionText, version.ToNormalizedString(), StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        $"Release version '{suppliedVersion}' is not a canonical NuGet version.")
        .ConfigureAwait(false);
    return 1;
}

string tag = $"v{versionText}";
bool tagRelease = string.Equals(eventName, "push", StringComparison.Ordinal) &&
    string.Equals(gitReference, $"refs/tags/{tag}", StringComparison.Ordinal);
if (string.Equals(eventName, "push", StringComparison.Ordinal) && !tagRelease)
{
    await Console.Error.WriteLineAsync(
        $"Release tag '{gitReference}' does not match version '{versionText}'.")
        .ConfigureAwait(false);
    return 1;
}

if (!tagRelease && !dryRun.Value)
{
    await Console.Error.WriteLineAsync(
        "A release can publish only from its exact version tag.")
        .ConfigureAwait(false);
    return 1;
}

bool publish = tagRelease && !dryRun.Value;
string timestamp = await ReadCommitTimestampAsync().ConfigureAwait(false);
string[] outputs =
[
    $"version={versionText}",
    $"tag={tag}",
    $"publish={(publish ? "true" : "false")}",
    $"prerelease={(version.IsPrerelease ? "true" : "false")}",
    $"timestamp={timestamp}"
];
foreach (string output in outputs)
{
    await Console.Out.WriteLineAsync(output).ConfigureAwait(false);
}

static async Task<string> ReadCommitTimestampAsync()
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("show");
    startInfo.ArgumentList.Add("--no-patch");
    startInfo.ArgumentList.Add("--format=%cI");
    startInfo.ArgumentList.Add("HEAD");
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("git did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = (await outputTask.ConfigureAwait(false)).Trim();
    string error = await errorTask.ConfigureAwait(false);
    if (process.ExitCode != 0 ||
        !DateTimeOffset.TryParse(
            output,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp))
    {
        throw new InvalidOperationException(
            $"Unable to read the release commit timestamp: {error.Trim()}");
    }

    return timestamp.UtcDateTime.ToString(
        "yyyy-MM-ddTHH:mm:ssZ",
        CultureInfo.InvariantCulture);
}

string? githubOutput = outputPath ?? Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
if (!string.IsNullOrWhiteSpace(githubOutput))
{
    await File.AppendAllLinesAsync(githubOutput, outputs).ConfigureAwait(false);
}

return 0;

static async Task<int> WriteUsageErrorAsync()
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Prepare-Release.cs -- " +
        "--version <version-or-vtag> --ref <git-ref> --event <event> " +
        "--dry-run <true|false> [--output <path>]")
        .ConfigureAwait(false);
    return 2;
}
