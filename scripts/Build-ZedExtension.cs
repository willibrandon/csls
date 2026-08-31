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
using System.Runtime.InteropServices;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Builds the verified csls extension package used by Zed.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Build-ZedExtension.cs -- " +
        "[--version <version>] [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

string version = "1.0.0";
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
        case "--output":
            outputPath = Path.GetFullPath(args[argumentIndex + 1]);
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

if (!NuGetVersion.TryParse(version, out NuGetVersion? parsedVersion) ||
    !string.Equals(version, parsedVersion.ToNormalizedString(), StringComparison.Ordinal))
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string? configuredArtifactsRoot = Environment.GetEnvironmentVariable(
        "CSLS_ARTIFACTS_ROOT");
    string artifactsRoot = string.IsNullOrWhiteSpace(configuredArtifactsRoot)
        ? Path.GetFullPath(Path.Join(repositoryRoot, "artifacts"))
        : Path.GetFullPath(configuredArtifactsRoot);
    string extensionRoot = Path.Join(repositoryRoot, "editors", "zed");
    string packagePath = RequirePathInsideArtifacts(
        artifactsRoot,
        outputPath ?? Path.Join(artifactsRoot, "editors", "zed", "csls"));
    string targetPath = Path.Join(artifactsRoot, "zed-extension", "target");
    string grammarSourceRoot = Path.Join(
        artifactsRoot,
        "zed-extension",
        "grammar-source");
    string csharpGrammarRoot = Path.Join(grammarSourceRoot, "c_sharp");
    string xmlGrammarRoot = Path.Join(grammarSourceRoot, "xml");
    string wasiClangPath = ResolveWasiClang(repositoryRoot);

    await RunCheckedAsync(
        "cargo",
        [
            "build",
            "--locked",
            "--release",
            "--target",
            "wasm32-wasip2",
            "--target-dir",
            targetPath
        ],
        extensionRoot).ConfigureAwait(false);

    string wasmPath = Path.Join(
        targetPath,
        "wasm32-wasip2",
        "release",
        "csls_zed.wasm");
    if (!File.Exists(wasmPath) || new FileInfo(wasmPath).Length == 0)
    {
        throw new InvalidDataException($"Cargo did not produce {wasmPath}.");
    }

    await CheckoutGrammarAsync(
        csharpGrammarRoot,
        "https://github.com/tree-sitter/tree-sitter-c-sharp").ConfigureAwait(false);
    await CheckoutGrammarAsync(
        xmlGrammarRoot,
        "https://github.com/tree-sitter-grammars/tree-sitter-xml").ConfigureAwait(false);

    RecreateDirectory(packagePath);
    CopyRequiredFile(extensionRoot, packagePath, "extension.toml");
    CopyRequiredFile(extensionRoot, packagePath, "LICENSE");
    CopyRequiredFile(extensionRoot, packagePath, "README.md");
    CopyDirectory(
        Path.Join(extensionRoot, "languages"),
        Path.Join(packagePath, "languages"));
    CopyDirectory(
        Path.Join(extensionRoot, "THIRD-PARTY-LICENSES"),
        Path.Join(packagePath, "THIRD-PARTY-LICENSES"));
    File.Copy(wasmPath, Path.Join(packagePath, "extension.wasm"));
    string grammarsPath = Path.Join(packagePath, "grammars");
    Directory.CreateDirectory(grammarsPath);
    await CompileGrammarAsync(
        wasiClangPath,
        "c_sharp",
        csharpGrammarRoot,
        Path.Join(grammarsPath, "c_sharp.wasm")).ConfigureAwait(false);
    await CompileGrammarAsync(
        wasiClangPath,
        "xml",
        Path.Join(xmlGrammarRoot, "xml"),
        Path.Join(grammarsPath, "xml.wasm")).ConfigureAwait(false);
    await SetManifestVersionAsync(
        Path.Join(packagePath, "extension.toml"),
        version).ConfigureAwait(false);
    VerifyPackage(packagePath, version);

    await Console.Out.WriteLineAsync(packagePath).ConfigureAwait(false);
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
        "Usage: dotnet run --file scripts/Build-ZedExtension.cs -- " +
        "[--version <version>] [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

static string RequirePathInsideArtifacts(string artifactsRoot, string path)
{
    string fullPath = Path.GetFullPath(path);
    string relativePath = Path.GetRelativePath(artifactsRoot, fullPath);
    if (string.Equals(relativePath, ".", StringComparison.Ordinal) ||
        Path.IsPathRooted(relativePath) ||
        string.Equals(relativePath, "..", StringComparison.Ordinal) ||
        relativePath.StartsWith(
            $"..{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The Zed extension output must be inside {artifactsRoot}.");
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

static string ResolveWasiClang(string repositoryRoot)
{
    string executableName = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
    string? configuredRoot = Environment.GetEnvironmentVariable("WASI_SDK_PATH");
    string toolsRoot = ScriptSupport.ResolveToolsRoot(repositoryRoot, null);
    string wasiRoot = Path.Join(toolsRoot, "wasi-sdk");
    string? provisionedPath = Directory.Exists(wasiRoot)
        ? Directory
            .EnumerateDirectories(wasiRoot)
            .Select(versionPath => (
                Path: versionPath,
                Version: Version.TryParse(Path.GetFileName(versionPath), out Version? parsed)
                    ? parsed
                    : new Version()))
            .OrderByDescending(static candidate => candidate.Version)
            .Select(versionPath => Path.Join(
                versionPath.Path,
                GetPlatform(),
                "bin",
                executableName))
            .FirstOrDefault(File.Exists)
        : null;
    string clangPath = string.IsNullOrWhiteSpace(configuredRoot)
        ? provisionedPath ?? string.Empty
        : Path.Join(Path.GetFullPath(configuredRoot), "bin", executableName);
    return File.Exists(clangPath)
        ? clangPath
        : throw new FileNotFoundException(
            "The WASI SDK is not provisioned. Run scripts/Provision-WasiSdk.cs.",
            clangPath);
}

static string GetPlatform()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    return (OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(),
        architecture) switch
    {
        (true, false, false, Architecture.X64) => "linux-x64",
        (true, false, false, Architecture.Arm64) => "linux-arm64",
        (false, true, false, Architecture.X64) => "osx-x64",
        (false, true, false, Architecture.Arm64) => "osx-arm64",
        (false, false, true, Architecture.X64) => "win-x64",
        (false, false, true, Architecture.Arm64) => "win-arm64",
        _ => throw new PlatformNotSupportedException(
            "The Zed extension build does not support this platform.")
    };
}

static async Task CheckoutGrammarAsync(
    string sourcePath,
    string repository)
{
    if (!Directory.Exists(Path.Join(sourcePath, ".git")))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await RunCheckedAsync("git", ["init", sourcePath], Environment.CurrentDirectory)
            .ConfigureAwait(false);
        await RunCheckedAsync(
            "git",
            ["remote", "add", "origin", repository],
            sourcePath).ConfigureAwait(false);
    }
    else
    {
        await RunCheckedAsync(
            "git",
            ["remote", "set-url", "origin", repository],
            sourcePath).ConfigureAwait(false);
    }

    await RunCheckedAsync(
        "git",
        ["fetch", "--depth", "1", "origin", "HEAD"],
        sourcePath).ConfigureAwait(false);
    await RunCheckedAsync(
        "git",
        ["checkout", "--detach", "FETCH_HEAD"],
        sourcePath).ConfigureAwait(false);
}

static async Task CompileGrammarAsync(
    string clangPath,
    string grammarName,
    string grammarRoot,
    string outputPath)
{
    string sourcePath = Path.Join(grammarRoot, "src");
    string parserPath = Path.Join(sourcePath, "parser.c");
    string scannerPath = Path.Join(sourcePath, "scanner.c");
    if (!File.Exists(parserPath))
    {
        throw new FileNotFoundException(
            $"The {grammarName} grammar does not contain a generated parser.",
            parserPath);
    }

    List<string> arguments =
    [
        "-fPIC",
        "-shared",
        "-Os",
        $"-Wl,--export=tree_sitter_{grammarName}",
        "-o",
        outputPath,
        "-I",
        sourcePath,
        parserPath
    ];
    if (File.Exists(scannerPath))
    {
        arguments.Add(scannerPath);
    }

    await RunCheckedAsync(clangPath, arguments, grammarRoot).ConfigureAwait(false);
    using FileStream grammar = File.OpenRead(outputPath);
    Span<byte> header = stackalloc byte[4];
    if (grammar.Read(header) != header.Length ||
        header[0] != 0 ||
        header[1] != (byte)'a' ||
        header[2] != (byte)'s' ||
        header[3] != (byte)'m')
    {
        throw new InvalidDataException(
            $"The {grammarName} compiler output is not WebAssembly.");
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
        throw new FileNotFoundException("A Zed extension input is missing.", sourcePath);
    }

    string destinationPath = Path.Join([destinationRoot, .. relativeSegments]);
    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    File.Copy(sourcePath, destinationPath);
}

static void CopyDirectory(string sourcePath, string destinationPath)
{
    if (!Directory.Exists(sourcePath))
    {
        throw new DirectoryNotFoundException(
            $"A Zed extension input directory is missing: {sourcePath}");
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

static async Task SetManifestVersionAsync(string manifestPath, string version)
{
    string[] lines = await File.ReadAllLinesAsync(manifestPath).ConfigureAwait(false);
    int versionLine = Array.FindIndex(
        lines,
        static line => line.StartsWith("version = ", StringComparison.Ordinal));
    if (versionLine < 0 || Array.FindLastIndex(
            lines,
            static line => line.StartsWith("version = ", StringComparison.Ordinal)) != versionLine)
    {
        throw new InvalidDataException(
            "The Zed extension manifest must contain exactly one version field.");
    }

    lines[versionLine] = $"version = \"{version}\"";
    await File.WriteAllLinesAsync(manifestPath, lines).ConfigureAwait(false);
}

static void VerifyPackage(string packagePath, string version)
{
    string manifestPath = Path.Join(packagePath, "extension.toml");
    string manifest = File.ReadAllText(manifestPath);
    if (!manifest.Contains("id = \"csls\"", StringComparison.Ordinal) ||
        !manifest.Contains($"version = \"{version}\"", StringComparison.Ordinal) ||
        !File.Exists(Path.Join(packagePath, "extension.wasm")) ||
        !File.Exists(Path.Join(packagePath, "grammars", "c_sharp.wasm")) ||
        !File.Exists(Path.Join(packagePath, "grammars", "xml.wasm")) ||
        !File.Exists(Path.Join(
            packagePath,
            "languages",
            "csharp",
            "highlights.scm")) ||
        !File.Exists(Path.Join(
            packagePath,
            "languages",
            "csproj",
            "tasks.json")) ||
        !File.Exists(Path.Join(
            packagePath,
            "languages",
            "slnf",
            "tasks.json")))
    {
        throw new InvalidDataException("The Zed extension package is incomplete.");
    }
}

static async Task RunCheckedAsync(
    string fileName,
    IEnumerable<string> arguments,
    string workingDirectory)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
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
        ?? throw new InvalidOperationException($"Failed to start {fileName}.");
    Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
    Task<string> standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await standardOutput.ConfigureAwait(false);
    string error = await standardError.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{fileName} failed with exit code {process.ExitCode}:{Environment.NewLine}" +
            output + error);
    }
}
