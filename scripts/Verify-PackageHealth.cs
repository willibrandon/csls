#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Text.Json;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Checks the restored solution for outdated, deprecated, and vulnerable packages.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-PackageHealth.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-PackageHealth.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

string repositoryRoot = FindRepositoryRoot();
string artifactsRoot = Path.Join(repositoryRoot, "artifacts");
string outputPath = args.Length == 2
    ? Path.GetFullPath(args[1])
    : Path.Join(artifactsRoot, "package-health");
RequirePathInsideArtifacts(artifactsRoot, outputPath);
Directory.CreateDirectory(outputPath);
foreach (string reportPath in Directory.EnumerateFiles(
    outputPath,
    "*.json",
    SearchOption.TopDirectoryOnly))
{
    File.Delete(reportPath);
}

(string Name, string[] Arguments)[] scopes =
[
    ("solution", []),
    ("file-apps", ["--file", "scripts/Assemble-ReleaseAssets.cs"])
];
(string Name, string[] Arguments)[] checks =
[
    ("outdated", ["--outdated"]),
    ("deprecated", ["--deprecated", "--include-transitive"]),
    ("vulnerable", ["--vulnerable", "--include-transitive"])
];
var failures = new List<string>();
foreach ((string scopeName, string[] scopeArguments) in scopes)
{
    foreach ((string checkName, string[] checkArguments) in checks)
    {
        int findingCount = await RunCheckAsync(
            repositoryRoot,
            outputPath,
            scopeName,
            checkName,
            [.. scopeArguments, .. checkArguments]).ConfigureAwait(false);
        if (findingCount != 0)
        {
            failures.Add(
                $"{findingCount} {checkName} package references in {scopeName}");
        }
    }
}

if (failures.Count != 0)
{
    await Console.Error.WriteLineAsync(
        $"Package health failed: {string.Join(", ", failures)}. See {outputPath}.")
        .ConfigureAwait(false);
    return 1;
}

await Console.Out.WriteLineAsync(
    "Verified package versions, deprecations, and vulnerabilities.")
    .ConfigureAwait(false);
return 0;

static async Task<int> RunCheckAsync(
    string repositoryRoot,
    string outputPath,
    string scopeName,
    string checkName,
    IReadOnlyList<string> arguments)
{
    string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    var startInfo = new ProcessStartInfo
    {
        FileName = dotnetPath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = repositoryRoot
    };
    startInfo.ArgumentList.Add("package");
    startInfo.ArgumentList.Add("list");
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    startInfo.ArgumentList.Add("--format");
    startInfo.ArgumentList.Add("json");
    startInfo.ArgumentList.Add("--output-version");
    startInfo.ArgumentList.Add("1");
    startInfo.ArgumentList.Add("--no-restore");

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("The .NET package health check did not start.");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    string reportPath = Path.Join(outputPath, $"{scopeName}-{checkName}.json");
    await File.WriteAllTextAsync(reportPath, output).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"dotnet package list for {scopeName} {checkName} failed with exit code " +
            $"{process.ExitCode}.{Environment.NewLine}{error}");
    }

    using var report = JsonDocument.Parse(output);
    return CountPackageEntries(report.RootElement);
}

static int CountPackageEntries(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array &&
                property.Name is "topLevelPackages" or "transitivePackages")
            {
                count += property.Value.GetArrayLength();
            }
            else
            {
                count += CountPackageEntries(property.Value);
            }
        }

        return count;
    }

    if (element.ValueKind != JsonValueKind.Array)
    {
        return 0;
    }

    int arrayCount = 0;
    foreach (JsonElement item in element.EnumerateArray())
    {
        arrayCount += CountPackageEntries(item);
    }

    return arrayCount;
}

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Environment.CurrentDirectory);
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

static void RequirePathInsideArtifacts(string artifactsRoot, string path)
{
    string relativePath = Path.GetRelativePath(artifactsRoot, path);
    if (relativePath.Length == 0 ||
        relativePath == "." ||
        relativePath == ".." ||
        relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        Path.IsPathFullyQualified(relativePath))
    {
        throw new InvalidOperationException(
            $"Package health output must be below {artifactsRoot}.");
    }
}
