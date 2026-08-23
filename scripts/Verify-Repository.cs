#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Verifies csls repository privacy, automation, lock-file, and dependency policies.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-Repository.cs").ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-Repository.cs").ConfigureAwait(false);
    return 2;
}

string repositoryRoot = FindRepositoryRoot();
IReadOnlyList<string> trackedPaths = await ReadTrackedPathsAsync(repositoryRoot)
    .ConfigureAwait(false);
var failures = new List<string>();
VerifyFilePolicy(trackedPaths, failures);
VerifyTrackedText(repositoryRoot, trackedPaths, failures);
VerifyDependencies(repositoryRoot, failures);
if (failures.Count != 0)
{
    foreach (string failure in failures.Order(StringComparer.Ordinal))
    {
        await Console.Error.WriteLineAsync(failure).ConfigureAwait(false);
    }

    return 1;
}

await Console.Out.WriteLineAsync(
    $"Verified {trackedPaths.Count} repository files and all direct package dependencies.")
    .ConfigureAwait(false);
return 0;

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}

static async Task<IReadOnlyList<string>> ReadTrackedPathsAsync(string repositoryRoot)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = repositoryRoot
    };
    startInfo.ArgumentList.Add("ls-files");
    startInfo.ArgumentList.Add("--cached");
    startInfo.ArgumentList.Add("--others");
    startInfo.ArgumentList.Add("--exclude-standard");
    startInfo.ArgumentList.Add("-z");
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Git did not start.");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"git ls-files failed with exit code {process.ExitCode}: {standardError.Trim()}");
    }

    return standardOutput
        .Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Order(StringComparer.Ordinal)
        .ToArray();
}

static void VerifyFilePolicy(
    IReadOnlyList<string> trackedPaths,
    ICollection<string> failures)
{
    string[] forbiddenAutomationExtensions =
    [
        ".bash",
        ".bat",
        ".cmd",
        ".ps1",
        ".psm1",
        ".sh"
    ];
    foreach (string trackedPath in trackedPaths)
    {
        string fileName = Path.GetFileName(trackedPath);
        if (string.Equals(fileName, "packages.lock.json", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Repository-wide package lock files are forbidden: {trackedPath}");
        }

        string extension = Path.GetExtension(trackedPath);
        if (forbiddenAutomationExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"Repository automation must be a file-based C# app: {trackedPath}");
        }
    }
}

static void VerifyTrackedText(
    string repositoryRoot,
    IReadOnlyList<string> trackedPaths,
    ICollection<string> failures)
{
    string upstreamOwner = "razz" + "matazz";
    string upstreamRepository = "csharp-" + "language-server";
    string upstreamQualifiedName = $"{upstreamOwner}/{upstreamRepository}";
    string issuePath = $"github.com/{upstreamQualifiedName}/issues/";
    string pullPath = $"github.com/{upstreamQualifiedName}/pull/";
    var qualifiedItem = new Regex(
        $"{Regex.Escape(upstreamQualifiedName)}#[0-9]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    foreach (string trackedPath in trackedPaths)
    {
        string fullPath = Path.Combine(repositoryRoot, trackedPath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        if (bytes.Contains((byte)0))
        {
            continue;
        }

        string text = File.ReadAllText(fullPath);
        if (text.Contains(issuePath, StringComparison.OrdinalIgnoreCase) ||
            text.Contains(pullPath, StringComparison.OrdinalIgnoreCase) ||
            qualifiedItem.IsMatch(text))
        {
            failures.Add($"Tracked text exposes private upstream work-item provenance: {trackedPath}");
        }

        if (trackedPath.StartsWith("src/", StringComparison.Ordinal) &&
            text.Contains("InternalsVisibleTo", StringComparison.Ordinal))
        {
            failures.Add($"Production assemblies must not expose internals to another assembly: {trackedPath}");
        }
    }
}

static void VerifyDependencies(string repositoryRoot, ICollection<string> failures)
{
    string[] approvedCorePrefixes =
    [
        "Microsoft.Build",
        "Microsoft.CodeAnalysis",
        "Microsoft.Extensions",
        "Microsoft.NET.StringTools",
        "Microsoft.SourceLink",
        "Microsoft.VisualStudio.Threading",
        "ModelContextProtocol",
        "NuGet.",
        "StreamJsonRpc",
        "System.CommandLine",
        "System.Security.Cryptography.Xml"
    ];
    string[] forbiddenPackages =
    [
        "FakeItEasy",
        "JustMock",
        "Moq",
        "NSubstitute",
        "Rhino.Mocks"
    ];
    foreach (string projectPath in Directory.EnumerateFiles(
        repositoryRoot,
        "*.csproj",
        SearchOption.AllDirectories))
    {
        if (projectPath.Contains(
            $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal))
        {
            continue;
        }

        var project = XDocument.Load(projectPath, LoadOptions.None);
        foreach (XElement reference in project.Descendants("PackageReference"))
        {
            string package = (string?)reference.Attribute("Include") ?? string.Empty;
            string relativeProjectPath = Path.GetRelativePath(repositoryRoot, projectPath);
            if (forbiddenPackages.Contains(package, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"Forbidden test dependency {package}: {relativeProjectPath}");
            }

            if (reference.Attribute("Version") is not null)
            {
                failures.Add(
                    $"Package versions must be managed centrally: {package} in {relativeProjectPath}");
            }

            if (relativeProjectPath.StartsWith($"src{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !approvedCorePrefixes.Any(prefix => package.StartsWith(
                    prefix,
                    StringComparison.Ordinal)))
            {
                failures.Add($"Unapproved core dependency {package}: {relativeProjectPath}");
            }
        }
    }
}
