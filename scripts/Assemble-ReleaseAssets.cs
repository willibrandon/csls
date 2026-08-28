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
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Assembles the complete release inventory, metadata, SBOM, and checksums.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Assemble-ReleaseAssets.cs -- " +
        "--version <version> --timestamp <utc> --input <path> --output <path> " +
        "--sbom-tool <path>")
        .ConfigureAwait(false);
    return 0;
}

string? version = null;
string? inputPath = null;
string? outputPath = null;
string? sbomToolPath = null;
string? generationTimestamp = null;
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
        case "--input":
            inputPath = Path.GetFullPath(value);
            break;
        case "--output":
            outputPath = Path.GetFullPath(value);
            break;
        case "--sbom-tool":
            sbomToolPath = Path.GetFullPath(value);
            break;
        case "--timestamp":
            generationTimestamp = value;
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

if (version is null ||
    !NuGetVersion.TryParse(version, out NuGetVersion? parsedVersion) ||
    !string.Equals(version, parsedVersion.ToNormalizedString(), StringComparison.Ordinal) ||
    inputPath is null ||
    outputPath is null ||
    sbomToolPath is null ||
    generationTimestamp is null ||
    !DateTimeOffset.TryParseExact(
        generationTimestamp,
        "yyyy-MM-ddTHH:mm:ssZ",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out _))
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string artifactsRoot = Path.GetFullPath(Path.Join(repositoryRoot, "artifacts"));
    string releaseInput = RequirePathInsideArtifacts(artifactsRoot, inputPath);
    string releaseOutput = RequirePathInsideArtifacts(artifactsRoot, outputPath);
    if (!File.Exists(sbomToolPath))
    {
        throw new FileNotFoundException("The Microsoft SBOM Tool is not provisioned.", sbomToolPath);
    }

    RecreateDirectory(releaseOutput);
    string publicOutput = Path.Join(releaseOutput, "public");
    Directory.CreateDirectory(publicOutput);
    string[] runtimeIdentifiers =
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
    (string PackageId, string CommandName, string Description)[] products =
    [
        ("csls", "csls", "Native AOT C# language server and command-line interface"),
        ("csls-mcp", "csls-mcp", "Native AOT MCP server for C# language intelligence")
    ];

    var expectedPackages = new HashSet<string>(StringComparer.Ordinal);
    var expectedArchives = new HashSet<string>(StringComparer.Ordinal);
    var expectedSymbols = new HashSet<string>(StringComparer.Ordinal);
    var expectedExtensions = new HashSet<string>(StringComparer.Ordinal);
    foreach ((string packageId, _, _) in products)
    {
        expectedPackages.Add($"{packageId}.{version}.nupkg");
        expectedPackages.Add($"{packageId}.any.{version}.nupkg");
        foreach (string runtimeIdentifier in runtimeIdentifiers)
        {
            expectedPackages.Add($"{packageId}.{runtimeIdentifier}.{version}.nupkg");
            string extension = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal)
                ? ".zip"
                : ".tar.gz";
            expectedArchives.Add(
                $"{packageId}-{version}-{runtimeIdentifier}{extension}");
            expectedSymbols.Add(
                $"{packageId}-{version}-{runtimeIdentifier}-symbols{extension}");
        }
    }

    foreach (string target in new[]
    {
        "win32-x64",
        "win32-arm64",
        "linux-x64",
        "linux-arm64",
        "alpine-x64",
        "alpine-arm64",
        "darwin-x64",
        "darwin-arm64",
        "web"
    })
    {
        expectedExtensions.Add($"csls-{version}-{target}.vsix");
    }

    CopyExactInventory(releaseInput, publicOutput, expectedPackages, "*.nupkg");
    CopyExactInventory(releaseInput, publicOutput, expectedArchives, "*.zip", "*.tar.gz");
    CopyExactInventory(releaseInput, publicOutput, expectedSymbols, "*.zip", "*.tar.gz");
    CopyExactInventory(releaseInput, publicOutput, expectedExtensions, "*.vsix");
    CopyContainerContexts(releaseInput, Path.Join(releaseOutput, "container"), products);

    foreach ((string packageId, string commandName, string description) in products)
    {
        await WriteHomebrewFormulaAsync(
            publicOutput,
            packageId,
            commandName,
            description,
            version).ConfigureAwait(false);
        await WriteScoopManifestAsync(
            publicOutput,
            packageId,
            commandName,
            description,
            version).ConfigureAwait(false);
        await WriteWinGetManifestsAsync(
            publicOutput,
            packageId,
            commandName,
            description,
            version).ConfigureAwait(false);
        await WriteNixExpressionAsync(
            publicOutput,
            packageId,
            commandName,
            description,
            version).ConfigureAwait(false);
        await ValidateDistributionMetadataAsync(
            publicOutput,
            packageId,
            version).ConfigureAwait(false);
    }

    string sbomRoot = Path.Join(releaseOutput, "sbom-work");
    Directory.CreateDirectory(sbomRoot);
    await RunCheckedAsync(
        sbomToolPath,
        [
            "generate",
            "-b",
            publicOutput,
            "-bc",
            repositoryRoot,
            "-pn",
            "csls",
            "-pv",
            version,
            "-ps",
            "willibrandon",
            "-nsb",
            "https://github.com/willibrandon/csls",
            "-gt",
            generationTimestamp,
            "-m",
            sbomRoot
        ],
        repositoryRoot).ConfigureAwait(false);
    string[] manifests =
    [
        .. Directory.EnumerateFiles(
            sbomRoot,
            "manifest.spdx.json",
            SearchOption.AllDirectories)
    ];
    if (manifests.Length != 1)
    {
        throw new InvalidDataException(
            $"Microsoft SBOM Tool produced {manifests.Length} SPDX manifests.");
    }

    string publicSbomPath = Path.Join(publicOutput, $"csls-{version}.spdx.json");
    File.Copy(manifests[0], publicSbomPath);
    ValidateSpdx(publicSbomPath, version);
    Directory.Delete(sbomRoot, recursive: true);
    await WriteChecksumsAsync(publicOutput).ConfigureAwait(false);
    await VerifyChecksumsAsync(publicOutput).ConfigureAwait(false);

    int assetCount = Directory.EnumerateFiles(
        publicOutput,
        "*",
        SearchOption.TopDirectoryOnly).Count();
    if (assetCount != 81)
    {
        throw new InvalidDataException(
            $"Expected 81 public release assets, found {assetCount}.");
    }

    await Console.Out.WriteLineAsync(
        $"Assembled and verified {assetCount} public release assets for {version}.")
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
        "Usage: dotnet run --file scripts/Assemble-ReleaseAssets.cs -- " +
        "--version <version> --timestamp <utc> --input <path> --output <path> " +
        "--sbom-tool <path>")
        .ConfigureAwait(false);
    return 2;
}

static void CopyExactInventory(
    string inputPath,
    string outputPath,
    IReadOnlySet<string> expectedNames,
    params string[] patterns)
{
    string[] candidates =
    [
        .. patterns
            .SelectMany(pattern => Directory.EnumerateFiles(
                inputPath,
                pattern,
                SearchOption.AllDirectories))
            .Where(path => expectedNames.Contains(Path.GetFileName(path)))
    ];
    var actualNames = candidates
        .Select(Path.GetFileName)
        .ToHashSet(StringComparer.Ordinal);
    if (candidates.Length != expectedNames.Count || !actualNames.SetEquals(expectedNames))
    {
        string missing = string.Join(", ", expectedNames.Except(actualNames).Order());
        throw new InvalidDataException(
            $"Release inventory is incomplete or duplicated. Missing: {missing}");
    }

    foreach (string sourcePath in candidates.Order(StringComparer.Ordinal))
    {
        File.Copy(sourcePath, Path.Join(outputPath, Path.GetFileName(sourcePath)));
    }
}

static void CopyContainerContexts(
    string inputPath,
    string outputPath,
    IReadOnlyList<(string PackageId, string CommandName, string Description)> products)
{
    foreach (string architecture in new[] { "amd64", "arm64" })
    {
        foreach ((string packageId, _, _) in products)
        {
            string suffix = Path.Join("container", architecture, packageId);
            string[] candidates =
            [
                .. Directory.EnumerateDirectories(
                    inputPath,
                    packageId,
                    SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(suffix, StringComparison.Ordinal))
            ];
            if (candidates.Length != 1)
            {
                throw new InvalidDataException(
                    $"Expected one {architecture} container context for {packageId}.");
            }

            CopyDirectory(candidates[0], Path.Join(outputPath, architecture, packageId));
        }
    }
}

static async Task WriteHomebrewFormulaAsync(
    string publicOutput,
    string packageId,
    string commandName,
    string description,
    string version)
{
    string className = string.Concat(
        packageId.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
    StringBuilder text = new StringBuilder()
        .AppendLine(CultureInfo.InvariantCulture, $"class {className} < Formula")
        .AppendLine(CultureInfo.InvariantCulture, $"  desc \"{description}\"")
        .AppendLine("  homepage \"https://willibrandon.github.io/csls/\"")
        .AppendLine(CultureInfo.InvariantCulture, $"  version \"{version}\"")
        .AppendLine("  license \"MIT\"");
    foreach ((string os, string runtimeIdentifier, string condition) in new[]
    {
        ("macos", "osx-arm64", "Hardware::CPU.arm?"),
        ("macos", "osx-x64", "Hardware::CPU.intel?"),
        ("linux", "linux-arm64", "Hardware::CPU.arm?"),
        ("linux", "linux-x64", "Hardware::CPU.intel?")
    })
    {
        string archiveName = $"{packageId}-{version}-{runtimeIdentifier}.tar.gz";
        string hash = await HashAsync(publicOutput, archiveName).ConfigureAwait(false);
        text.AppendLine(CultureInfo.InvariantCulture, $"  on_{os} do")
            .AppendLine(CultureInfo.InvariantCulture, $"    if {condition}")
            .AppendLine(CultureInfo.InvariantCulture, $"      url \"{ReleaseUrl(version, archiveName)}\"")
            .AppendLine(CultureInfo.InvariantCulture, $"      sha256 \"{hash}\"")
            .AppendLine("    end")
            .AppendLine("  end");
    }

    text.AppendLine()
        .AppendLine("  def install")
        .AppendLine("    libexec.install Dir[\"*\"]")
        .AppendLine(CultureInfo.InvariantCulture, $"    bin.install_symlink libexec/\"{commandName}\"")
        .AppendLine("  end")
        .AppendLine()
        .AppendLine("  test do")
        .AppendLine(CultureInfo.InvariantCulture, $"    assert_match version.to_s, shell_output(\"#{{bin}}/{commandName} --version\")")
        .AppendLine("  end")
        .AppendLine("end");
    await File.WriteAllTextAsync(
        Path.Join(publicOutput, $"homebrew-{packageId}.rb"),
        text.ToString()).ConfigureAwait(false);
}

static async Task WriteScoopManifestAsync(
    string publicOutput,
    string packageId,
    string commandName,
    string description,
    string version)
{
    var architecture = new JsonObject();
    foreach ((string key, string runtimeIdentifier) in new[]
    {
        ("64bit", "win-x64"),
        ("arm64", "win-arm64"),
        ("32bit", "win-x86")
    })
    {
        string archiveName = $"{packageId}-{version}-{runtimeIdentifier}.zip";
        architecture[key] = new JsonObject
        {
            ["url"] = ReleaseUrl(version, archiveName),
            ["hash"] = await HashAsync(publicOutput, archiveName).ConfigureAwait(false)
        };
    }

    var manifest = new JsonObject
    {
        ["version"] = version,
        ["description"] = description,
        ["homepage"] = "https://willibrandon.github.io/csls/",
        ["license"] = "MIT",
        ["architecture"] = architecture,
        ["bin"] = $"{commandName}.exe"
    };
    await File.WriteAllTextAsync(
        Path.Join(publicOutput, $"scoop-{packageId}.json"),
        manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
        Environment.NewLine).ConfigureAwait(false);
}

static async Task WriteWinGetManifestsAsync(
    string publicOutput,
    string packageId,
    string commandName,
    string description,
    string version)
{
    const string schemaVersion = "1.12.0";
    string identifier = $"willibrandon.{packageId}";
    string filePrefix = $"winget-{identifier}";
    string versionManifest = $"""
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.version.{schemaVersion}.schema.json
        PackageIdentifier: {identifier}
        PackageVersion: {version}
        DefaultLocale: en-US
        ManifestType: version
        ManifestVersion: {schemaVersion}
        """ + Environment.NewLine;
    string localeManifest = $"""
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.{schemaVersion}.schema.json
        PackageIdentifier: {identifier}
        PackageVersion: {version}
        PackageLocale: en-US
        Publisher: willibrandon
        PublisherUrl: https://github.com/willibrandon
        PackageName: {packageId}
        PackageUrl: https://github.com/willibrandon/csls
        License: MIT
        LicenseUrl: https://github.com/willibrandon/csls/blob/main/LICENSE
        ShortDescription: {description}
        Tags:
          - csharp
          - dotnet
          - language-server
          - native-aot
        ManifestType: defaultLocale
        ManifestVersion: {schemaVersion}
        """ + Environment.NewLine;
    StringBuilder installerManifest = new StringBuilder()
        .AppendLine(CultureInfo.InvariantCulture, $"# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.{schemaVersion}.schema.json")
        .AppendLine(CultureInfo.InvariantCulture, $"PackageIdentifier: {identifier}")
        .AppendLine(CultureInfo.InvariantCulture, $"PackageVersion: {version}")
        .AppendLine("InstallerType: zip")
        .AppendLine("NestedInstallerType: portable")
        .AppendLine("Installers:");
    foreach ((string architecture, string runtimeIdentifier) in new[]
    {
        ("x64", "win-x64"),
        ("arm64", "win-arm64"),
        ("x86", "win-x86")
    })
    {
        string archiveName = $"{packageId}-{version}-{runtimeIdentifier}.zip";
        installerManifest.AppendLine(CultureInfo.InvariantCulture, $"  - Architecture: {architecture}")
            .AppendLine(CultureInfo.InvariantCulture, $"    InstallerUrl: {ReleaseUrl(version, archiveName)}")
            .AppendLine(CultureInfo.InvariantCulture, $"    InstallerSha256: {(await HashAsync(publicOutput, archiveName).ConfigureAwait(false)).ToUpperInvariant()}")
            .AppendLine("    NestedInstallerFiles:")
            .AppendLine(CultureInfo.InvariantCulture, $"      - RelativeFilePath: {commandName}.exe")
            .AppendLine(CultureInfo.InvariantCulture, $"        PortableCommandAlias: {commandName}");
    }

    installerManifest.AppendLine("ManifestType: installer")
        .AppendLine(CultureInfo.InvariantCulture, $"ManifestVersion: {schemaVersion}");
    await File.WriteAllTextAsync(
        Path.Join(publicOutput, $"{filePrefix}.yaml"),
        versionManifest).ConfigureAwait(false);
    await File.WriteAllTextAsync(
        Path.Join(publicOutput, $"{filePrefix}.locale.en-US.yaml"),
        localeManifest).ConfigureAwait(false);
    await File.WriteAllTextAsync(
        Path.Join(publicOutput, $"{filePrefix}.installer.yaml"),
        installerManifest.ToString()).ConfigureAwait(false);
}

static async Task WriteNixExpressionAsync(
    string publicOutput,
    string packageId,
    string commandName,
    string description,
    string version)
{
    StringBuilder text = new StringBuilder()
        .AppendLine("{ lib, stdenv, fetchurl }:")
        .AppendLine("let")
        .AppendLine("  sources = {");
    foreach ((string nixSystem, string runtimeIdentifier) in new[]
    {
        ("x86_64-linux", "linux-x64"),
        ("aarch64-linux", "linux-arm64"),
        ("x86_64-darwin", "osx-x64"),
        ("aarch64-darwin", "osx-arm64")
    })
    {
        string archiveName = $"{packageId}-{version}-{runtimeIdentifier}.tar.gz";
        string hash = await HashAsync(publicOutput, archiveName).ConfigureAwait(false);
        string sriHash = Convert.ToBase64String(Convert.FromHexString(hash));
        text.AppendLine(CultureInfo.InvariantCulture, $"    \"{nixSystem}\" = {{")
            .AppendLine(CultureInfo.InvariantCulture, $"      url = \"{ReleaseUrl(version, archiveName)}\";")
            .AppendLine(CultureInfo.InvariantCulture, $"      hash = \"sha256-{sriHash}\";")
            .AppendLine("    };");
    }

    text.AppendLine("  };")
        .AppendLine("  source = sources.${stdenv.hostPlatform.system} or")
        .AppendLine(CultureInfo.InvariantCulture, $"    (throw \"{packageId} does not support ${{stdenv.hostPlatform.system}}\");")
        .AppendLine("in")
        .AppendLine("stdenv.mkDerivation {")
        .AppendLine(CultureInfo.InvariantCulture, $"  pname = \"{packageId}\";")
        .AppendLine(CultureInfo.InvariantCulture, $"  version = \"{version}\";")
        .AppendLine("  src = fetchurl source;")
        .AppendLine("  sourceRoot = \".\";")
        .AppendLine("  dontConfigure = true;")
        .AppendLine("  dontBuild = true;")
        .AppendLine("  installPhase = ''")
        .AppendLine("    runHook preInstall")
        .AppendLine(CultureInfo.InvariantCulture, $"    mkdir -p $out/libexec/{packageId} $out/bin")
        .AppendLine(CultureInfo.InvariantCulture, $"    cp -R . $out/libexec/{packageId}")
        .AppendLine(CultureInfo.InvariantCulture, $"    ln -s $out/libexec/{packageId}/{commandName} $out/bin/{commandName}")
        .AppendLine("    runHook postInstall")
        .AppendLine("  '';")
        .AppendLine("  meta = {")
        .AppendLine(CultureInfo.InvariantCulture, $"    description = \"{description}\";")
        .AppendLine("    homepage = \"https://willibrandon.github.io/csls/\";")
        .AppendLine("    license = lib.licenses.mit;")
        .AppendLine("    mainProgram = \"" + commandName + "\";")
        .AppendLine("    platforms = builtins.attrNames sources;")
        .AppendLine("  };")
        .AppendLine("}");
    await File.WriteAllTextAsync(
        Path.Join(publicOutput, $"nix-{packageId}.nix"),
        text.ToString()).ConfigureAwait(false);
}

static async Task ValidateDistributionMetadataAsync(
    string publicOutput,
    string packageId,
    string version)
{
    string homebrew = await File.ReadAllTextAsync(
        Path.Join(publicOutput, $"homebrew-{packageId}.rb")).ConfigureAwait(false);
    string winget = await File.ReadAllTextAsync(
        Path.Join(publicOutput, $"winget-willibrandon.{packageId}.installer.yaml"))
        .ConfigureAwait(false);
    string nix = await File.ReadAllTextAsync(
        Path.Join(publicOutput, $"nix-{packageId}.nix")).ConfigureAwait(false);
    using var scoop = JsonDocument.Parse(await File.ReadAllTextAsync(
        Path.Join(publicOutput, $"scoop-{packageId}.json")).ConfigureAwait(false));
    foreach ((string runtimeIdentifier, string scoopArchitecture) in new[]
    {
        ("win-x64", "64bit"),
        ("win-arm64", "arm64"),
        ("win-x86", "32bit")
    })
    {
        string archiveName = $"{packageId}-{version}-{runtimeIdentifier}.zip";
        string url = ReleaseUrl(version, archiveName);
        string hash = await HashAsync(publicOutput, archiveName).ConfigureAwait(false);
        JsonElement entry = scoop.RootElement
            .GetProperty("architecture")
            .GetProperty(scoopArchitecture);
        if (!string.Equals(entry.GetProperty("url").GetString(), url, StringComparison.Ordinal) ||
            !string.Equals(entry.GetProperty("hash").GetString(), hash, StringComparison.Ordinal) ||
            !winget.Contains(url, StringComparison.Ordinal) ||
            !winget.Contains(hash.ToUpperInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Windows distribution metadata does not match {archiveName}.");
        }
    }

    foreach (string runtimeIdentifier in new[]
    {
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64"
    })
    {
        string archiveName = $"{packageId}-{version}-{runtimeIdentifier}.tar.gz";
        string url = ReleaseUrl(version, archiveName);
        string hash = await HashAsync(publicOutput, archiveName).ConfigureAwait(false);
        string sriHash = Convert.ToBase64String(Convert.FromHexString(hash));
        if (!homebrew.Contains(url, StringComparison.Ordinal) ||
            !homebrew.Contains(hash, StringComparison.Ordinal) ||
            !nix.Contains(url, StringComparison.Ordinal) ||
            !nix.Contains($"sha256-{sriHash}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unix distribution metadata does not match {archiveName}.");
        }
    }
}

static async Task<string> HashAsync(string publicOutput, string fileName) =>
    await ScriptSupport.ComputeSha256Async(
        Path.Join(publicOutput, fileName),
        CancellationToken.None).ConfigureAwait(false);

static string ReleaseUrl(string version, string fileName) =>
    $"https://github.com/willibrandon/csls/releases/download/v{version}/{fileName}";

static void ValidateSpdx(string path, string version)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    JsonElement root = document.RootElement;
    if (!string.Equals(
            root.GetProperty("spdxVersion").GetString(),
            "SPDX-2.2",
            StringComparison.Ordinal) ||
        !root.GetProperty("name").GetString()!.Contains("csls", StringComparison.OrdinalIgnoreCase) ||
        !root.GetRawText().Contains(version, StringComparison.Ordinal))
    {
        throw new InvalidDataException("The generated SPDX manifest has unexpected metadata.");
    }
}

static async Task WriteChecksumsAsync(string publicOutput)
{
    string[] files =
    [
        .. Directory.EnumerateFiles(publicOutput, "*", SearchOption.TopDirectoryOnly)
            .Where(static path => !string.Equals(
                Path.GetFileName(path),
                "SHA256SUMS",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
    ];
    var checksums = new StringBuilder();
    foreach (string path in files)
    {
        string hash = await ScriptSupport.ComputeSha256Async(
            path,
            CancellationToken.None).ConfigureAwait(false);
        checksums.Append(hash).Append("  ").AppendLine(Path.GetFileName(path));
    }

    await File.WriteAllTextAsync(
        Path.Join(publicOutput, "SHA256SUMS"),
        checksums.ToString()).ConfigureAwait(false);
}

static async Task VerifyChecksumsAsync(string publicOutput)
{
    string checksumPath = Path.Join(publicOutput, "SHA256SUMS");
    string[] lines = await File.ReadAllLinesAsync(checksumPath).ConfigureAwait(false);
    foreach (string[] fields in lines.Select(
        static line => line.Split("  ", 2, StringSplitOptions.None)))
    {
        if (fields.Length != 2)
        {
            throw new InvalidDataException("SHA256SUMS contains a malformed entry.");
        }

        string actualHash = await ScriptSupport.ComputeSha256Async(
            Path.Join(publicOutput, fields[1]),
            CancellationToken.None).ConfigureAwait(false);
        if (!string.Equals(fields[0], actualHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SHA-256 verification failed for {fields[1]}.");
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
        string destinationFile = Path.Join(destinationPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        File.Copy(sourceFile, destinationFile);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(sourceFile));
        }
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
        ?? throw new InvalidOperationException($"{executablePath} did not start.");
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
