#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package NuGet.Versioning
#:package SharpCompress
#:include ScriptSupport.cs

using NuGet.Versioning;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Builds a VS Code extension for one native platform or the web host.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Build-VsCodeExtension.cs -- " +
        "--version <version> --target <target> [--server <directory>] --output <vsix>")
        .ConfigureAwait(false);
    return 0;
}

string? version = null;
string? target = null;
string? serverPath = null;
string? outputPath = null;
for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex += 2)
{
    if (argumentIndex + 1 >= args.Length)
    {
        return await WriteUsageErrorAsync().ConfigureAwait(false);
    }

    switch (args[argumentIndex])
    {
        case "--version":
            version = args[argumentIndex + 1];
            break;
        case "--target":
            target = args[argumentIndex + 1];
            break;
        case "--server":
            serverPath = Path.GetFullPath(args[argumentIndex + 1]);
            break;
        case "--output":
            outputPath = Path.GetFullPath(args[argumentIndex + 1]);
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

string[] supportedTargets =
[
    "win32-x64",
    "win32-arm64",
    "linux-x64",
    "linux-arm64",
    "alpine-x64",
    "alpine-arm64",
    "darwin-x64",
    "darwin-arm64",
    "web"
];
bool isWebTarget = string.Equals(target, "web", StringComparison.Ordinal);
if (version is null ||
    !NuGetVersion.TryParse(version, out NuGetVersion? parsedVersion) ||
    !string.Equals(version, parsedVersion.ToNormalizedString(), StringComparison.Ordinal) ||
    target is null ||
    !supportedTargets.Contains(target, StringComparer.Ordinal) ||
    !isWebTarget && serverPath is null ||
    outputPath is null)
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

string extensionVersion = parsedVersion.Version.ToString(3);

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string extensionSource = Path.Join(repositoryRoot, "editors", "vscode");
    if (!isWebTarget)
    {
        RequireServer(serverPath!);
    }
    string outputDirectory = Path.GetDirectoryName(outputPath)
        ?? throw new InvalidOperationException($"The output path has no parent: {outputPath}");
    Directory.CreateDirectory(outputDirectory);
    await RunCheckedAsync(
        ResolveNpmExecutable(),
        ["ci", "--ignore-scripts"],
        extensionSource).ConfigureAwait(false);
    await RunCheckedAsync(
        ResolveNpmExecutable(),
        ["run", "check"],
        extensionSource).ConfigureAwait(false);
    if (isWebTarget)
    {
        await RunCheckedAsync(
            ResolveNpmExecutable(),
            ["run", "compile:browser"],
            extensionSource).ConfigureAwait(false);
        await RunCheckedAsync(
            ResolveNpmExecutable(),
            ["run", "compile:worker"],
            extensionSource).ConfigureAwait(false);
    }
    else
    {
        await RunCheckedAsync(
            ResolveNpmExecutable(),
            ["run", "compile:node"],
            extensionSource).ConfigureAwait(false);
    }

    string stagingPath = Path.Join(
        repositoryRoot,
        "artifacts",
        "vscode-extension",
        "staging",
        $"{target}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingPath);
    try
    {
        CopyRequiredFile(extensionSource, stagingPath, "package.json");
        CopyRequiredFile(extensionSource, stagingPath, "README.md");
        CopyRequiredFile(extensionSource, stagingPath, "CHANGELOG.md");
        CopyRequiredFile(extensionSource, stagingPath, "LICENSE");
        CopyRequiredFile(extensionSource, stagingPath, "language-configuration.json");
        CopyRequiredFile(extensionSource, stagingPath, ".vscodeignore");
        if (isWebTarget)
        {
            CopyRequiredFile(extensionSource, stagingPath, "dist", "browserExtension.cjs");
            CopyDirectory(
                Path.Join(extensionSource, "dist", "browserServer"),
                Path.Join(stagingPath, "dist", "browserServer"));
        }
        else
        {
            CopyRequiredFile(extensionSource, stagingPath, "dist", "extension.cjs");
            CopyServer(serverPath!, Path.Join(stagingPath, "server"));
        }
        CopyRequiredFile(extensionSource, stagingPath, "media", "icon.png");
        await PreparePackageManifestAsync(
            Path.Join(stagingPath, "package.json"),
            extensionVersion,
            isWebTarget).ConfigureAwait(false);

        string vscePath = Path.Join(
            extensionSource,
            "node_modules",
            "@vscode",
            "vsce",
            "vsce");
        List<string> packageArguments =
        [
            vscePath,
            "package",
            "--no-dependencies",
            "--target",
            target,
            "--out",
            outputPath
        ];
        if (parsedVersion.IsPrerelease)
        {
            packageArguments.Add("--pre-release");
        }

        await RunCheckedAsync("node", packageArguments, stagingPath).ConfigureAwait(false);
    }
    finally
    {
        Directory.Delete(stagingPath, recursive: true);
    }

    if (!File.Exists(outputPath))
    {
        throw new InvalidDataException($"The VS Code extension was not produced: {outputPath}");
    }

    await VerifyPackageAsync(outputPath, extensionVersion, isWebTarget).ConfigureAwait(false);

    await Console.Out.WriteLineAsync(outputPath).ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException or
    Win32Exception)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task<int> WriteUsageErrorAsync()
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Build-VsCodeExtension.cs -- " +
        "--version <version> --target <target> [--server <directory>] --output <vsix>")
        .ConfigureAwait(false);
    return 2;
}

static void RequireServer(string serverPath)
{
    string executableName = OperatingSystem.IsWindows() ? "csls.exe" : "csls";
    if (!Directory.Exists(serverPath) || !File.Exists(Path.Join(serverPath, executableName)))
    {
        throw new InvalidDataException(
            $"The server directory does not contain {executableName}: {serverPath}");
    }

    string workerPath = Path.Join(serverPath, "workers", "server", "csls-worker.dll");
    if (!File.Exists(workerPath))
    {
        throw new InvalidDataException(
            $"The server directory does not contain the managed Roslyn worker: {serverPath}");
    }
}

static void CopyRequiredFile(
    string sourceRoot,
    string destinationRoot,
    params string[] relativeSegments)
{
    string sourcePath = Path.Join([sourceRoot, .. relativeSegments]);
    if (!File.Exists(sourcePath))
    {
        throw new FileNotFoundException("A VS Code extension input is missing.", sourcePath);
    }

    string destinationPath = Path.Join([destinationRoot, .. relativeSegments]);
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    File.Copy(sourcePath, destinationPath);
}

static void CopyServer(string sourcePath, string destinationPath)
{
    string[] excludedExtensions =
    [
        ".dbg",
        ".dwarf",
        ".map",
        ".mibc",
        ".mstat",
        ".pdb",
        ".xml"
    ];
    foreach (string sourceFile in Directory.EnumerateFiles(
        sourcePath,
        "*",
        SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(sourcePath, sourceFile);
        if (relativePath.StartsWith(
            Path.Join("workers", "cli") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal))
        {
            continue;
        }

        if (excludedExtensions.Contains(
            Path.GetExtension(sourceFile),
            StringComparer.OrdinalIgnoreCase))
        {
            continue;
        }

        string destinationFile = Path.Join(destinationPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        File.Copy(sourceFile, destinationFile);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(sourceFile));
        }
    }
}

static void CopyDirectory(string sourcePath, string destinationPath)
{
    if (!Directory.Exists(sourcePath))
    {
        throw new DirectoryNotFoundException(
            $"A VS Code extension input directory is missing: {sourcePath}");
    }

    foreach (string sourceFile in Directory.EnumerateFiles(
        sourcePath,
        "*",
        SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(sourcePath, sourceFile);
        string destinationFile = Path.Join(destinationPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        File.Copy(sourceFile, destinationFile);
    }
}

static async Task PreparePackageManifestAsync(
    string packagePath,
    string version,
    bool isWebTarget)
{
    JsonNode package = JsonNode.Parse(
        await File.ReadAllTextAsync(packagePath).ConfigureAwait(false))
        ?? throw new InvalidDataException("The VS Code package manifest is empty.");
    package["version"] = version;
    package.AsObject().Remove("scripts");
    package.AsObject().Remove("devDependencies");
    if (isWebTarget)
    {
        package.AsObject().Remove("main");
        package.AsObject().Remove("extensionDependencies");
        package.AsObject().Remove("x-dotnet-acquire");
        package["engines"]?.AsObject().Remove("node");
        package["contributes"]?.AsObject().Remove("debuggers");
        package["contributes"]?["configuration"]?["properties"]
            ?.AsObject()
            .Remove("csls.server.path");
    }
    else
    {
        package.AsObject().Remove("browser");
        package["capabilities"]?.AsObject().Remove("virtualWorkspaces");
    }
    await File.WriteAllTextAsync(
        packagePath,
        package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n")
        .ConfigureAwait(false);
}

static string ResolveNpmExecutable() => OperatingSystem.IsWindows() ? "npm.cmd" : "npm";

static async Task VerifyPackageAsync(
    string packagePath,
    string version,
    bool isWebTarget)
{
    using ZipArchive archive = await ZipFile.OpenReadAsync(
        packagePath,
        CancellationToken.None).ConfigureAwait(false);
    ZipArchiveEntry manifestEntry = archive.GetEntry("extension/package.json")
        ?? throw new InvalidDataException("The VS Code package does not contain its manifest.");
    using Stream manifestStream = await manifestEntry.OpenAsync(
        CancellationToken.None).ConfigureAwait(false);
    using JsonDocument manifest = await JsonDocument.ParseAsync(manifestStream).ConfigureAwait(false);
    JsonElement root = manifest.RootElement;
    RequireJsonString(root, "version", version);
    RequireJsonString(
        root,
        isWebTarget ? "browser" : "main",
        isWebTarget ? "./dist/browserExtension.cjs" : "./dist/extension.cjs");
    RejectJsonProperty(root, isWebTarget ? "main" : "browser");
    RejectJsonProperty(root, "scripts");
    RejectJsonProperty(root, "devDependencies");

    JsonElement contributes = RequireJsonObject(root, "contributes");
    if (isWebTarget)
    {
        RejectJsonProperty(root, "extensionDependencies");
        RejectJsonProperty(root, "x-dotnet-acquire");
        RejectJsonProperty(RequireJsonObject(root, "engines"), "node");
        RejectJsonProperty(contributes, "debuggers");
        JsonElement properties = RequireJsonObject(
            RequireJsonObject(contributes, "configuration"),
            "properties");
        RejectJsonProperty(properties, "csls.server.path");
        RequireArchiveEntry(archive, "extension/dist/browserExtension.cjs");
        RequireArchiveEntry(archive, "extension/dist/browserServer/cslsBrowserWorker.js");
        RequireArchiveMatch(archive, "extension/dist/browserServer/_framework/", ".wasm");
        RejectArchiveEntry(archive, "extension/dist/extension.cjs");
        RejectArchivePrefix(archive, "extension/server/");
    }
    else
    {
        RequireJsonProperty(root, "extensionDependencies");
        RequireJsonProperty(root, "x-dotnet-acquire");
        RequireJsonProperty(contributes, "debuggers");
        RejectJsonProperty(RequireJsonObject(root, "capabilities"), "virtualWorkspaces");
        RequireArchiveEntry(archive, "extension/dist/extension.cjs");
        RequireArchiveMatch(archive, "extension/server/", "csls");
        RequireArchiveEntry(archive, "extension/server/workers/server/csls-worker.dll");
        RejectArchiveEntry(archive, "extension/dist/browserExtension.cjs");
        RejectArchivePrefix(archive, "extension/dist/browserServer/");
    }
}

static JsonElement RequireJsonObject(JsonElement parent, string propertyName)
{
    if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
        value.ValueKind is not JsonValueKind.Object)
    {
        throw new InvalidDataException(
            $"The VS Code package manifest is missing object property '{propertyName}'.");
    }

    return value;
}

static void RequireJsonProperty(JsonElement parent, string propertyName)
{
    if (!parent.TryGetProperty(propertyName, out _))
    {
        throw new InvalidDataException(
            $"The VS Code package manifest is missing property '{propertyName}'.");
    }
}

static void RequireJsonString(
    JsonElement parent,
    string propertyName,
    string expectedValue)
{
    if (!parent.TryGetProperty(propertyName, out JsonElement value) ||
        value.ValueKind is not JsonValueKind.String ||
        !string.Equals(value.GetString(), expectedValue, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The VS Code package manifest property '{propertyName}' is invalid.");
    }
}

static void RejectJsonProperty(JsonElement parent, string propertyName)
{
    if (parent.TryGetProperty(propertyName, out _))
    {
        throw new InvalidDataException(
            $"The VS Code package manifest contains unsupported property '{propertyName}'.");
    }
}

static void RequireArchiveEntry(ZipArchive archive, string entryName)
{
    if (archive.GetEntry(entryName) is null)
    {
        throw new InvalidDataException(
            $"The VS Code package is missing required entry '{entryName}'.");
    }
}

static void RequireArchiveMatch(
    ZipArchive archive,
    string prefix,
    string value)
{
    if (!archive.Entries.Any(entry =>
        entry.FullName.StartsWith(prefix, StringComparison.Ordinal) &&
        entry.FullName.Contains(value, StringComparison.Ordinal)))
    {
        throw new InvalidDataException(
            $"The VS Code package has no entry matching '{prefix}*{value}*'.");
    }
}

static void RejectArchiveEntry(ZipArchive archive, string entryName)
{
    if (archive.GetEntry(entryName) is not null)
    {
        throw new InvalidDataException(
            $"The VS Code package contains unsupported entry '{entryName}'.");
    }
}

static void RejectArchivePrefix(ZipArchive archive, string prefix)
{
    if (archive.Entries.Any(entry =>
        entry.FullName.StartsWith(prefix, StringComparison.Ordinal)))
    {
        throw new InvalidDataException(
            $"The VS Code package contains unsupported entries under '{prefix}'.");
    }
}

static async Task RunCheckedAsync(
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
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    await Console.Out.WriteAsync(output).ConfigureAwait(false);
    await Console.Error.WriteAsync(error).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}.");
    }
}
