#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Text.Json;
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
VerifyRepositoryRoot(repositoryRoot, failures);
VerifyFilePolicy(trackedPaths, failures);
VerifyTrackedText(repositoryRoot, trackedPaths, failures);
VerifyDependencies(repositoryRoot, failures);
VerifyPackageSources(repositoryRoot, failures);
VerifyGitHubActionReferences(repositoryRoot, failures);
VerifyVsCodeActivationEvents(repositoryRoot, failures);
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

static void VerifyRepositoryRoot(
    string repositoryRoot,
    ICollection<string> failures)
{
    string[] forbiddenDirectories =
    [
        "BenchmarkDotNet.Artifacts",
        "TestResults"
    ];
    foreach (string directoryName in Directory
        .EnumerateDirectories(repositoryRoot, "*", SearchOption.TopDirectoryOnly)
        .Select(Path.GetFileName)
        .OfType<string>()
        .Where(name => forbiddenDirectories.Contains(
            name,
            StringComparer.OrdinalIgnoreCase)))
    {
        failures.Add($"Build and test artifacts belong under artifacts/: {directoryName}");
    }

    string[] forbiddenExtensions =
    [
        ".binlog",
        ".coverage",
        ".trx"
    ];
    foreach (string fileName in Directory
        .EnumerateFiles(repositoryRoot, "*", SearchOption.TopDirectoryOnly)
        .Where(path => forbiddenExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase))
        .Select(Path.GetFileName)
        .OfType<string>())
    {
        failures.Add($"Build and test artifacts belong under artifacts/: {fileName}");
    }
}

static void VerifyVsCodeActivationEvents(
    string repositoryRoot,
    ICollection<string> failures)
{
    string[] manifestPaths =
    [
        Path.Join("editors", "vscode", "package.json"),
        Path.Join("tests", "vscode", "package.json"),
        Path.Join("tests", "vscode", "remote-resolver", "package.json")
    ];
    foreach (string manifestPath in manifestPaths)
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Join(repositoryRoot, manifestPath)));
        if (!manifest.RootElement.TryGetProperty("activationEvents", out JsonElement events))
        {
            continue;
        }

        if (events.EnumerateArray().Any(
            static item => string.Equals(item.GetString(), "*", StringComparison.Ordinal)))
        {
            failures.Add($"VS Code wildcard activation is forbidden: {manifestPath}");
        }
    }
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
    string privateRazorSdkPath = "Microsoft.NET.Sdk.Razor/" + "source-generators";
    var qualifiedItem = new Regex(
        $"{Regex.Escape(upstreamQualifiedName)}#[0-9]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    foreach (string trackedPath in trackedPaths)
    {
        string fullPath = Path.Join(repositoryRoot, trackedPath);
        if (!File.Exists(fullPath))
        {
            continue;
        }

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

        if (text.Contains(privateRazorSdkPath, StringComparison.Ordinal))
        {
            failures.Add(
                $"Razor assemblies must come from centrally managed Microsoft packages: " +
                trackedPath);
        }
    }
}

static void VerifyDependencies(string repositoryRoot, ICollection<string> failures)
{
    string[] approvedCorePrefixes =
    [
        "Hex1b",
        "Microsoft.AspNetCore.Razor",
        "Microsoft.Bcl",
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
    IEnumerable<string> dependencyFiles = Directory.EnumerateFiles(
            repositoryRoot,
            "*.csproj",
            SearchOption.AllDirectories)
        .Append(Path.Join(repositoryRoot, "Directory.Build.props"));
    foreach (string projectPath in dependencyFiles)
    {
        if (IsGeneratedPath(projectPath))
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

        foreach (XElement download in project.Descendants("PackageDownload"))
        {
            string package = (string?)download.Attribute("Include") ?? string.Empty;
            string version = (string?)download.Attribute("Version") ?? string.Empty;
            string relativeProjectPath = Path.GetRelativePath(repositoryRoot, projectPath);
            if (!approvedCorePrefixes.Any(prefix => package.StartsWith(
                    prefix,
                    StringComparison.Ordinal)))
            {
                failures.Add($"Unapproved downloaded dependency {package}: {relativeProjectPath}");
            }

            if (version.Length < 3 || version[0] != '[' || version[^1] != ']')
            {
                failures.Add(
                    $"Downloaded dependency versions must be exact: {package} in {relativeProjectPath}");
            }
        }
    }
}

static void VerifyPackageSources(string repositoryRoot, ICollection<string> failures)
{
    string configurationPath = Path.Join(repositoryRoot, "NuGet.Config");
    if (!File.Exists(configurationPath))
    {
        failures.Add("NuGet.Config must define the Microsoft Razor package source.");
        return;
    }

    var configuration = XDocument.Load(configurationPath, LoadOptions.None);
    XElement? source = configuration
        .Descendants("packageSources")
        .Elements("add")
        .SingleOrDefault(element => string.Equals(
            (string?)element.Attribute("key"),
            "dotnet-tools",
            StringComparison.Ordinal));
    const string expectedSource =
        "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json";
    if (!string.Equals(
            (string?)source?.Attribute("value"),
            expectedSource,
            StringComparison.Ordinal))
    {
        failures.Add("NuGet.Config must use the public Microsoft dotnet-tools feed.");
    }

    var mappedPackages = configuration
        .Descendants("packageSource")
        .Where(element => string.Equals(
            (string?)element.Attribute("key"),
            "dotnet-tools",
            StringComparison.Ordinal))
        .Elements("package")
        .Select(element => (string?)element.Attribute("pattern") ?? string.Empty)
        .ToHashSet(StringComparer.Ordinal);
    string[] requiredPackages =
    [
        "Microsoft.AspNetCore.Razor.Utilities.Shared",
        "Microsoft.CodeAnalysis.Razor.Compiler"
    ];
    foreach (string package in requiredPackages.Where(
        package => !mappedPackages.Contains(package)))
    {
        failures.Add($"NuGet.Config must map {package} to dotnet-tools.");
    }
}

static bool IsGeneratedPath(string path) =>
    path.Contains(
        $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
        StringComparison.Ordinal) ||
    path.Contains(
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        StringComparison.Ordinal) ||
    path.Contains(
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        StringComparison.Ordinal);

static void VerifyGitHubActionReferences(
    string repositoryRoot,
    ICollection<string> failures)
{
    string workflowDirectory = Path.Join(repositoryRoot, ".github", "workflows");
    foreach (string workflowPath in Directory.EnumerateFiles(
        workflowDirectory,
        "*.yml",
        SearchOption.TopDirectoryOnly))
    {
        int lineNumber = 0;
        foreach (string line in File.ReadLines(workflowPath))
        {
            lineNumber++;
            string trimmedLine = line.TrimStart();
            if (!trimmedLine.StartsWith("uses:", StringComparison.Ordinal))
            {
                continue;
            }

            string[] referenceParts = trimmedLine["uses:".Length..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (referenceParts.Length == 0)
            {
                string relativePath = Path.GetRelativePath(repositoryRoot, workflowPath);
                failures.Add($"GitHub Action reference is empty: {relativePath}:{lineNumber}");
                continue;
            }

            string reference = referenceParts[0];
            if (reference.StartsWith("./", StringComparison.Ordinal))
            {
                continue;
            }

            int separatorIndex = reference.LastIndexOf('@');
            string revision = separatorIndex >= 0 ? reference[(separatorIndex + 1)..] : string.Empty;
            bool immutableCommit = revision.Length == 40 && revision.All(Uri.IsHexDigit);
            string versionCore = revision.TrimStart('v').Split('-', '+')[0];
            bool exactVersion = versionCore.Any(static character => character == '.') &&
                Version.TryParse(versionCore, out _);
            if (immutableCommit || exactVersion)
            {
                string relativePath = Path.GetRelativePath(repositoryRoot, workflowPath);
                failures.Add(
                    $"GitHub Actions must follow a moving major or release channel: " +
                    $"{relativePath}:{lineNumber} ({reference})");
            }
        }
    }
}
