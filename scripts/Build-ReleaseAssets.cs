#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package NuGet.Versioning
#:package SharpCompress
#:include ScriptSupport.cs

using NuGet.Versioning;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Builds verified NuGet, standalone, symbol, and container release assets for one RID.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Build-ReleaseAssets.cs -- " +
        "--version <version> --runtime <rid> --output <path> " +
        "[--validation <execute|package>] [--target-dotnet-root <path>]")
        .ConfigureAwait(false);
    return 0;
}

string? version = null;
string? runtimeIdentifier = null;
string? outputPath = null;
string validation = "execute";
string? targetDotnetRoot = null;
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
            version = value;
            break;
        case "--runtime":
            runtimeIdentifier = value;
            break;
        case "--output":
            outputPath = value;
            break;
        case "--validation" when value is "execute" or "package":
            validation = value;
            break;
        case "--target-dotnet-root":
            targetDotnetRoot = Path.GetFullPath(value);
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

string[] supportedRuntimeIdentifiers =
[
    "win-x64",
    "win-arm64",
    "win-x86",
    "linux-x64",
    "linux-arm64",
    "linux-musl-x64",
    "linux-musl-arm64",
    "osx-x64",
    "osx-arm64"
];
if (version is null ||
    !NuGetVersion.TryParse(version, out NuGetVersion? parsedVersion) ||
    !string.Equals(version, parsedVersion.ToNormalizedString(), StringComparison.Ordinal) ||
    runtimeIdentifier is null ||
    !supportedRuntimeIdentifiers.Contains(runtimeIdentifier, StringComparer.Ordinal) ||
    outputPath is null)
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string artifactsRoot = Path.GetFullPath(Path.Join(repositoryRoot, "artifacts"));
    string releaseOutput = RequirePathInsideArtifacts(artifactsRoot, outputPath);
    string workRoot = RequirePathInsideArtifacts(
        artifactsRoot,
        Path.Join(artifactsRoot, "release-work", runtimeIdentifier));
    RecreateDirectory(releaseOutput);
    RecreateDirectory(workRoot);
    try
    {
        await VerifyPackagesAsync(
            repositoryRoot,
            version,
            runtimeIdentifier,
            validation,
            targetDotnetRoot).ConfigureAwait(false);
        CopyPackages(repositoryRoot, releaseOutput, version, runtimeIdentifier);

        string cslsPublishPath = Path.Join(workRoot, "publish", "csls");
        await PublishAsync(
            repositoryRoot,
            "src/Csls.App/Csls.App.csproj",
            cslsPublishPath,
            version,
            runtimeIdentifier).ConfigureAwait(false);
        Task editorAssets = string.Equals(
            runtimeIdentifier,
            "win-x86",
            StringComparison.Ordinal)
            ? Task.CompletedTask
            : BuildVsCodeExtensionAsync(
                repositoryRoot,
                cslsPublishPath,
                releaseOutput,
                version,
                runtimeIdentifier);
        Task cslsArchives = BuildProductArchivesAsync(
            cslsPublishPath,
            releaseOutput,
            workRoot,
            "csls",
            "csls",
            version,
            runtimeIdentifier);
        Task mcpAssets = BuildProductAssetsAsync(
            repositoryRoot,
            releaseOutput,
            workRoot,
            "csls-mcp",
            "csls-mcp",
            "src/Csls.Mcp/Csls.Mcp.csproj",
            version,
            runtimeIdentifier);
        await Task.WhenAll(editorAssets, cslsArchives, mcpAssets).ConfigureAwait(false);
    }
    finally
    {
        Directory.Delete(workRoot, recursive: true);
    }

    await Console.Out.WriteLineAsync(
        $"Built verified release assets for {runtimeIdentifier} in {releaseOutput}.")
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

static async Task BuildVsCodeExtensionAsync(
    string repositoryRoot,
    string publishPath,
    string releaseOutput,
    string version,
    string runtimeIdentifier)
{
    string target = runtimeIdentifier switch
    {
        "win-x64" => "win32-x64",
        "win-arm64" => "win32-arm64",
        "linux-x64" => "linux-x64",
        "linux-arm64" => "linux-arm64",
        "linux-musl-x64" => "alpine-x64",
        "linux-musl-arm64" => "alpine-arm64",
        "osx-x64" => "darwin-x64",
        "osx-arm64" => "darwin-arm64",
        _ => throw new InvalidDataException(
            $"The runtime has no VS Code extension target: {runtimeIdentifier}")
    };
    string extensionPath = Path.Join(
        releaseOutput,
        "editors",
        $"csls-{version}-{target}.vsix");
    await RunCheckedAsync(
        "dotnet",
        [
            "run",
            "--file",
            "scripts/Build-VsCodeExtension.cs",
            "--",
            "--version",
            version,
            "--target",
            target,
            "--server",
            publishPath,
            "--output",
            extensionPath
        ],
        repositoryRoot).ConfigureAwait(false);
    if (string.Equals(runtimeIdentifier, "linux-x64", StringComparison.Ordinal))
    {
        string webExtensionPath = Path.Join(
            releaseOutput,
            "editors",
            $"csls-{version}-web.vsix");
        await RunCheckedAsync(
            "dotnet",
            [
                "run",
                "--file",
                "scripts/Build-VsCodeExtension.cs",
                "--",
                "--version",
                version,
                "--target",
                "web",
                "--output",
                webExtensionPath
            ],
            repositoryRoot).ConfigureAwait(false);
    }
}

static async Task BuildProductAssetsAsync(
    string repositoryRoot,
    string releaseOutput,
    string workRoot,
    string packageId,
    string commandName,
    string projectPath,
    string version,
    string runtimeIdentifier)
{
    string publishPath = Path.Join(workRoot, "publish", packageId);
    await PublishAsync(
        repositoryRoot,
        projectPath,
        publishPath,
        version,
        runtimeIdentifier).ConfigureAwait(false);
    await BuildProductArchivesAsync(
        publishPath,
        releaseOutput,
        workRoot,
        packageId,
        commandName,
        version,
        runtimeIdentifier).ConfigureAwait(false);
}

static async Task<int> WriteUsageErrorAsync()
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Build-ReleaseAssets.cs -- " +
        "--version <version> --runtime <rid> --output <path> " +
        "[--validation <execute|package>] [--target-dotnet-root <path>]")
        .ConfigureAwait(false);
    return 2;
}

static async Task VerifyPackagesAsync(
    string repositoryRoot,
    string version,
    string runtimeIdentifier,
    string validation,
    string? targetDotnetRoot)
{
    List<string> arguments =
    [
        "run",
        "--file",
        "scripts/Verify-ToolPackages.cs",
        "--",
        "--version",
        version,
        "--runtime",
        runtimeIdentifier,
        "--validation",
        validation
    ];
    if (targetDotnetRoot is not null)
    {
        arguments.Add("--target-dotnet-root");
        arguments.Add(targetDotnetRoot);
    }

    await RunCheckedAsync("dotnet", arguments, repositoryRoot).ConfigureAwait(false);
}

static void CopyPackages(
    string repositoryRoot,
    string releaseOutput,
    string version,
    string runtimeIdentifier)
{
    string packageSource = Path.Join(
        repositoryRoot,
        "artifacts",
        "tool-package-verification",
        "packages");
    string packageDestination = Path.Join(releaseOutput, "packages");
    Directory.CreateDirectory(packageDestination);
    foreach (string packageId in new[] { "csls", "csls-mcp" })
    {
        CopyRequiredFile(
            Path.Join(packageSource, $"{packageId}.{runtimeIdentifier}.{version}.nupkg"),
            packageDestination);
        if (string.Equals(runtimeIdentifier, "linux-x64", StringComparison.Ordinal))
        {
            CopyRequiredFile(
                Path.Join(packageSource, $"{packageId}.{version}.nupkg"),
                packageDestination);
            CopyRequiredFile(
                Path.Join(packageSource, $"{packageId}.any.{version}.nupkg"),
                packageDestination);
        }
    }
}

static async Task PublishAsync(
    string repositoryRoot,
    string projectPath,
    string publishPath,
    string version,
    string runtimeIdentifier)
{
    Directory.CreateDirectory(publishPath);
    await RunCheckedAsync(
        "dotnet",
        [
            "publish",
            projectPath,
            "--configuration",
            "Release",
            "--runtime",
            runtimeIdentifier,
            "--self-contained",
            "true",
            "--output",
            publishPath,
            $"-p:Version={version}",
            $"-p:PackageVersion={version}",
            "-p:DebugSymbols=true",
            "-p:IlcGenerateMstatFile=true"
        ],
        repositoryRoot).ConfigureAwait(false);
}

static async Task BuildProductArchivesAsync(
    string publishPath,
    string releaseOutput,
    string workRoot,
    string packageId,
    string commandName,
    string version,
    string runtimeIdentifier)
{
    string runtimeRoot = Path.Join(workRoot, "runtime", packageId);
    string symbolRoot = Path.Join(workRoot, "symbols", packageId);
    Directory.CreateDirectory(runtimeRoot);
    Directory.CreateDirectory(symbolRoot);
    int runtimeFileCount = 0;
    int symbolFileCount = 0;
    foreach (string sourcePath in Directory.EnumerateFiles(
        publishPath,
        "*",
        SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(publishPath, sourcePath);
        bool symbol = IsSymbol(relativePath);
        CopyPreservingMode(
            sourcePath,
            Path.Join(symbol ? symbolRoot : runtimeRoot, relativePath));
        if (symbol)
        {
            symbolFileCount++;
        }
        else
        {
            runtimeFileCount++;
        }
    }

    string executableName = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
        ? commandName + ".exe"
        : commandName;
    if (runtimeFileCount == 0 ||
        !File.Exists(Path.Join(runtimeRoot, executableName)) ||
        symbolFileCount == 0)
    {
        throw new InvalidDataException(
            $"The {packageId} {runtimeIdentifier} publish output is incomplete.");
    }

    string archiveExtension = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
        ? ".zip"
        : ".tar.gz";
    string archivePath = Path.Join(
        releaseOutput,
        "archives",
        $"{packageId}-{version}-{runtimeIdentifier}{archiveExtension}");
    string symbolArchivePath = Path.Join(
        releaseOutput,
        "symbols",
        $"{packageId}-{version}-{runtimeIdentifier}-symbols{archiveExtension}");
    await CreateArchiveAsync(runtimeRoot, archivePath).ConfigureAwait(false);
    await CreateArchiveAsync(symbolRoot, symbolArchivePath).ConfigureAwait(false);
    await VerifyArchiveAsync(runtimeRoot, archivePath, workRoot).ConfigureAwait(false);
    await VerifyArchiveAsync(symbolRoot, symbolArchivePath, workRoot).ConfigureAwait(false);

    if (runtimeIdentifier is "linux-x64" or "linux-arm64")
    {
        string architecture = runtimeIdentifier.EndsWith("x64", StringComparison.Ordinal)
            ? "amd64"
            : "arm64";
        CopyDirectory(
            runtimeRoot,
            Path.Join(releaseOutput, "container", architecture, packageId));
    }
}

static bool IsSymbol(string relativePath) =>
    relativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
    relativePath.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) ||
    relativePath.EndsWith(".dwarf", StringComparison.OrdinalIgnoreCase) ||
    relativePath.Split(Path.DirectorySeparatorChar)
        .Any(static segment => segment.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase));

static async Task CreateArchiveAsync(string sourcePath, string archivePath)
{
    string archiveDirectory = Path.GetDirectoryName(archivePath) ??
        throw new InvalidOperationException($"Archive path has no directory: {archivePath}");
    Directory.CreateDirectory(archiveDirectory);
    if (archivePath.EndsWith(".zip", StringComparison.Ordinal))
    {
        await ZipFile.CreateFromDirectoryAsync(
            sourcePath,
            archivePath,
            CompressionLevel.Optimal,
            includeBaseDirectory: false,
            CancellationToken.None).ConfigureAwait(false);
        return;
    }

    using FileStream output = new(
        archivePath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 131_072,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var compressed = new GZipStream(
        output,
        CompressionLevel.Optimal,
        leaveOpen: true);
    await TarFile.CreateFromDirectoryAsync(
        sourcePath,
        compressed,
        includeBaseDirectory: false,
        CancellationToken.None).ConfigureAwait(false);
}

static async Task VerifyArchiveAsync(
    string sourcePath,
    string archivePath,
    string workRoot)
{
    string extractionPath = Path.Join(
        workRoot,
        "archive-verification",
        Path.GetFileName(archivePath));
    Directory.CreateDirectory(extractionPath);
    await ScriptSupport.ExtractArchiveAsync(
        archivePath,
        extractionPath,
        CancellationToken.None).ConfigureAwait(false);
    string[] sourceFiles =
    [
        .. Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(sourcePath, path))
            .Order(StringComparer.Ordinal)
    ];
    string[] extractedFiles =
    [
        .. Directory.EnumerateFiles(extractionPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(extractionPath, path))
            .Order(StringComparer.Ordinal)
    ];
    if (!sourceFiles.SequenceEqual(extractedFiles, StringComparer.Ordinal))
    {
        throw new InvalidDataException($"Archive inventory mismatch: {archivePath}");
    }

    foreach (string relativePath in sourceFiles)
    {
        string sourceHash = await ScriptSupport.ComputeSha256Async(
            Path.Join(sourcePath, relativePath),
            CancellationToken.None).ConfigureAwait(false);
        string extractedHash = await ScriptSupport.ComputeSha256Async(
            Path.Join(extractionPath, relativePath),
            CancellationToken.None).ConfigureAwait(false);
        if (!string.Equals(sourceHash, extractedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Archive content mismatch for {relativePath} in {archivePath}.");
        }
    }
}

static void CopyDirectory(string sourcePath, string destinationPath)
{
    foreach (string sourceFile in Directory.EnumerateFiles(
        sourcePath,
        "*",
        SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(sourcePath, sourceFile);
        CopyPreservingMode(sourceFile, Path.Join(destinationPath, relativePath));
    }
}

static void CopyRequiredFile(string sourcePath, string destinationDirectory)
{
    if (!File.Exists(sourcePath))
    {
        throw new FileNotFoundException("A release package was not produced.", sourcePath);
    }

    Directory.CreateDirectory(destinationDirectory);
    File.Copy(sourcePath, Path.Join(destinationDirectory, Path.GetFileName(sourcePath)));
}

static void CopyPreservingMode(string sourcePath, string destinationPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    File.Copy(sourcePath, destinationPath);
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(destinationPath, File.GetUnixFileMode(sourcePath));
    }
}

static string RequirePathInsideArtifacts(string artifactsRoot, string path)
{
    string fullPath = Path.GetFullPath(path);
    string prefix = Path.TrimEndingDirectorySeparator(artifactsRoot) +
        Path.DirectorySeparatorChar;
    if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"The release path must be inside the repository artifacts directory: {fullPath}");
    }

    return fullPath;
}

static void RecreateDirectory(string path)
{
    if (Directory.Exists(path))
    {
        Directory.Delete(path, recursive: true);
    }

    Directory.CreateDirectory(path);
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
