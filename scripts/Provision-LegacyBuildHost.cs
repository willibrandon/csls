#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

const string MonoRepositoryPath = "/etc/apt/sources.list.d/mono-official-stable.list";
const string MonoPackageBaseUrl = "https://download.mono-project.com/repo/debian/";
const string MonoPackageIndexPath = "dists/stable-buster/main";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs and verifies the platform build host used for legacy .NET Framework projects.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: sudo dotnet run --file scripts/Provision-LegacyBuildHost.cs (Linux)")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "       dotnet run --file scripts/Provision-LegacyBuildHost.cs (macOS/Windows)")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: sudo dotnet run --file scripts/Provision-LegacyBuildHost.cs (Linux)")
        .ConfigureAwait(false);
    await Console.Error.WriteLineAsync(
        "       dotnet run --file scripts/Provision-LegacyBuildHost.cs (macOS/Windows)")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string hostDescription;
    if (OperatingSystem.IsWindows())
    {
        hostDescription = await VerifyVisualStudioBuildHostAsync().ConfigureAwait(false);
    }
    else if (OperatingSystem.IsMacOS())
    {
        await RunCheckedAsync(
            "brew",
            ["install", "--cask", "mono-mdk"]).ConfigureAwait(false);
        hostDescription = await VerifyMonoBuildHostAsync().ConfigureAwait(false);
    }
    else if (OperatingSystem.IsLinux())
    {
        await ProvisionLinuxMonoAsync().ConfigureAwait(false);
        hostDescription = await VerifyMonoBuildHostAsync().ConfigureAwait(false);
    }
    else
    {
        throw new PlatformNotSupportedException(
            $"Legacy build-host provisioning does not support {Environment.OSVersion}.");
    }

    await Console.Out.WriteLineAsync(hostDescription).ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    HttpRequestException or
    IOException or
    InvalidDataException or
    InvalidOperationException or
    PlatformNotSupportedException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task ProvisionLinuxMonoAsync()
{
    if (!File.Exists("/etc/debian_version"))
    {
        throw new PlatformNotSupportedException(
            "Automatic Mono provisioning currently supports Debian and Ubuntu.");
    }

    string identifier = ReadOperatingSystemIdentifier();
    if (!string.Equals(identifier, "ubuntu", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(identifier, "debian", StringComparison.OrdinalIgnoreCase))
    {
        throw new PlatformNotSupportedException(
            $"Automatic Mono provisioning does not support Linux distribution '{identifier}'.");
    }

    await RunPrivilegedAsync("rm", ["--force", MonoRepositoryPath]).ConfigureAwait(false);
    await RunPrivilegedAsync("apt-get", ["update"]).ConfigureAwait(false);
    await RunPrivilegedAsync(
        "apt-get",
        ["install", "--yes", "--no-install-recommends", "mono-complete"])
        .ConfigureAwait(false);

    (IReadOnlyList<string> msBuildPackages, string monoRoslynPackage) =
        await DownloadMonoBuildHostPackagesAsync().ConfigureAwait(false);
    string compatibleMonoRoslynPackage = await CreateCompatibleMonoRoslynPackageAsync(
        monoRoslynPackage).ConfigureAwait(false);
    await RunPrivilegedAsync(
        "apt-get",
        [
            "install",
            "--yes",
            "--no-install-recommends",
            .. msBuildPackages,
            compatibleMonoRoslynPackage
        ])
        .ConfigureAwait(false);
}

static async Task<(IReadOnlyList<string> MsBuildPackages, string MonoRoslynPackage)>
    DownloadMonoBuildHostPackagesAsync()
{
    string architecture = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "i386",
        Architecture.Arm => "armhf",
        _ => throw new PlatformNotSupportedException(
            $"Mono MSBuild is unavailable for {RuntimeInformation.ProcessArchitecture}.")
    };
    string packageIndex = await DownloadMonoPackageIndexAsync(architecture)
        .ConfigureAwait(false);
    string[] packageNames =
    [
        "msbuild",
        "msbuild-sdkresolver",
        "msbuild-libhostfxr",
        "mono-roslyn"
    ];
    (string FileName, Uri Source, string Sha256)[] packages =
    [
        .. packageNames.Select(packageName => ResolveMonoPackage(
            packageIndex,
            packageName))
    ];

    string cacheDirectory = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "csls",
        "legacy-build-host");
    Directory.CreateDirectory(cacheDirectory);
    using var httpClient = new HttpClient();
    var packagePaths = new List<string>(packages.Length);
    foreach ((string fileName, Uri packageSource, string sha256) in packages)
    {
        string packagePath = Path.Join(cacheDirectory, fileName);
        if (!File.Exists(packagePath) ||
            !await HasSha256Async(packagePath, sha256).ConfigureAwait(false))
        {
            string partialPath = packagePath + ".partial";
            File.Delete(partialPath);
            try
            {
                using Stream download = await httpClient.GetStreamAsync(
                    packageSource).ConfigureAwait(false);
                using (var destination = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await download.CopyToAsync(destination).ConfigureAwait(false);
                }

                if (!await HasSha256Async(partialPath, sha256).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        $"Downloaded Mono package failed SHA-256 verification: {fileName}");
                }

                File.Move(partialPath, packagePath, overwrite: true);
            }
            finally
            {
                File.Delete(partialPath);
            }
        }

        packagePaths.Add(packagePath);
    }

    return (packagePaths[..^1], packagePaths[^1]);
}

static async Task<string> DownloadMonoPackageIndexAsync(string architecture)
{
    var indexUri = new Uri(
        $"{MonoPackageBaseUrl}{MonoPackageIndexPath}/binary-{architecture}/Packages.gz");
    using var httpClient = new HttpClient();
    using Stream response = await httpClient.GetStreamAsync(indexUri).ConfigureAwait(false);
    using var decompressed = new GZipStream(response, CompressionMode.Decompress);
    using var reader = new StreamReader(decompressed);
    return await reader.ReadToEndAsync().ConfigureAwait(false);
}

static (string FileName, Uri Source, string Sha256) ResolveMonoPackage(
    string packageIndex,
    string packageName)
{
    string[] paragraphs = packageIndex.Split(
        ["\r\n\r\n", "\n\n"],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    string[] matches =
    [
        .. paragraphs.Where(paragraph => string.Equals(
            ReadPackageField(paragraph, "Package"),
            packageName,
            StringComparison.Ordinal))
    ];
    if (matches.Length != 1)
    {
        throw new InvalidDataException(
            $"The Mono stable package index contains {matches.Length} entries for " +
            $"{packageName}; exactly one is required.");
    }

    string relativePath = ReadPackageField(matches[0], "Filename")
        ?? throw new InvalidDataException(
            $"The Mono stable package {packageName} has no filename.");
    string sha256 = ReadPackageField(matches[0], "SHA256")
        ?? throw new InvalidDataException(
            $"The Mono stable package {packageName} has no SHA-256 digest.");
    return (
        Path.GetFileName(relativePath),
        new Uri(MonoPackageBaseUrl + relativePath),
        sha256);
}

static string? ReadPackageField(string paragraph, string fieldName)
{
    string prefix = fieldName + ":";
    string? line = paragraph
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
    return line?[prefix.Length..].Trim();
}

static async Task<string> CreateCompatibleMonoRoslynPackageAsync(string sourcePackagePath)
{
    string cacheDirectory = Path.GetDirectoryName(sourcePackagePath)
        ?? throw new InvalidDataException(
            $"The Mono Roslyn package has no parent directory: {sourcePackagePath}");
    string packageRoot = Path.Join(cacheDirectory, "csls-mono-roslyn-package");
    string outputPackagePath = Path.Join(
        cacheDirectory,
        "csls-mono-roslyn_all.deb");
    if (Directory.Exists(packageRoot))
    {
        Directory.Delete(packageRoot, recursive: true);
    }

    Directory.CreateDirectory(packageRoot);
    await RunCheckedAsync(
        "dpkg-deb",
        ["--raw-extract", sourcePackagePath, packageRoot]).ConfigureAwait(false);
    string controlDirectory = Path.Join(packageRoot, "DEBIAN");
    foreach (string controlEntry in Directory.EnumerateFileSystemEntries(controlDirectory))
    {
        if (File.Exists(controlEntry) || File.GetAttributes(controlEntry).HasFlag(
                FileAttributes.ReparsePoint))
        {
            File.Delete(controlEntry);
        }
        else
        {
            Directory.Delete(controlEntry, recursive: true);
        }
    }

    string version = (await RunCheckedAsync(
        "dpkg-deb",
        ["--field", sourcePackagePath, "Version"]).ConfigureAwait(false)).Trim();
    if (string.IsNullOrWhiteSpace(version))
    {
        throw new InvalidDataException(
            $"The Mono Roslyn package has no version: {sourcePackagePath}");
    }

    string controlText = $$"""
        Package: csls-mono-roslyn
        Version: {{version}}
        Architecture: all
        Maintainer: csls contributors <noreply@localhost>
        Depends: mono-runtime, mono-devel, msbuild
        Provides: mono-roslyn (= {{version}})
        Conflicts: mono-roslyn
        Replaces: mono-roslyn
        Section: devel
        Priority: optional
        Homepage: https://www.mono-project.com/
        Description: Mono Roslyn compiler payload for csls legacy workspaces
         Mono compiler targets required by Roslyn's Mono MSBuild host.

        """;
    await File.WriteAllTextAsync(
        Path.Join(controlDirectory, "control"),
        controlText).ConfigureAwait(false);
    File.Delete(outputPackagePath);
    await RunCheckedAsync(
        "dpkg-deb",
        ["--build", "--root-owner-group", packageRoot, outputPackagePath])
        .ConfigureAwait(false);
    return outputPackagePath;
}

static async Task<bool> HasSha256Async(string path, string expectedSha256)
{
    using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read);
    byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
    return string.Equals(
        Convert.ToHexStringLower(hash),
        expectedSha256,
        StringComparison.Ordinal);
}

static string ReadOperatingSystemIdentifier()
{
    const string operatingSystemReleasePath = "/etc/os-release";
    string identifierLine = File.ReadLines(operatingSystemReleasePath)
        .FirstOrDefault(static line => line.StartsWith("ID=", StringComparison.Ordinal))
        ?? throw new InvalidDataException(
            $"{operatingSystemReleasePath} does not declare a distribution identifier.");
    return identifierLine[3..].Trim().Trim('"');
}

static async Task<string> VerifyVisualStudioBuildHostAsync()
{
    string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    string vsWherePath = Path.Join(
        programFiles,
        "Microsoft Visual Studio",
        "Installer",
        "vswhere.exe");
    if (!File.Exists(vsWherePath))
    {
        throw new FileNotFoundException(
            "Visual Studio Installer discovery is unavailable.",
            vsWherePath);
    }

    string installationPath = (await RunCheckedAsync(
        vsWherePath,
        [
            "-latest",
            "-products",
            "*",
            "-requires",
            "Microsoft.Component.MSBuild",
            "-property",
            "installationPath"
        ]).ConfigureAwait(false)).Trim();
    if (string.IsNullOrWhiteSpace(installationPath))
    {
        throw new InvalidDataException(
            "Visual Studio or Build Tools with MSBuild is not installed.");
    }

    string msBuildPath = Path.Join(
        installationPath,
        "MSBuild",
        "Current",
        "Bin",
        "MSBuild.exe");
    if (!File.Exists(msBuildPath))
    {
        throw new FileNotFoundException("Visual Studio MSBuild was not found.", msBuildPath);
    }

    string version = (await RunCheckedAsync(msBuildPath, ["-version", "-nologo"])
        .ConfigureAwait(false)).Trim();
    return $"Visual Studio MSBuild {version} at {msBuildPath}";
}

static async Task<string> VerifyMonoBuildHostAsync()
{
    string monoVersion = (await RunCheckedAsync("mono", ["--version"])
        .ConfigureAwait(false)).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
    string buildPath = FindMonoBuildCommand();
    string msBuildDirectory = FindMonoMsBuildDirectory();
    string buildVersion = (await RunCheckedAsync(
        buildPath,
        ["-version", "-nologo"]).ConfigureAwait(false)).Trim();
    return
        $"{monoVersion}; Mono MSBuild {buildVersion} at {buildPath}; assemblies at {msBuildDirectory}";
}

static string FindMonoBuildCommand()
{
    string? path = Environment.GetEnvironmentVariable("PATH");
    if (path is not null)
    {
        string[] pathDirectories = path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? buildPath = pathDirectories
            .Select(directory => Path.Join(directory, "msbuild"))
            .FirstOrDefault(File.Exists);
        if (buildPath is not null)
        {
            return buildPath;
        }
    }

    throw new FileNotFoundException(
        "Mono is installed without the msbuild executable required by Roslyn.");
}

static string FindMonoMsBuildDirectory()
{
    string[] candidates =
    [
        "/usr/lib/mono/msbuild/Current/bin",
        "/usr/lib/mono/msbuild/15.0/bin",
        "/usr/local/lib/mono/msbuild/Current/bin",
        "/usr/local/lib/mono/msbuild/15.0/bin",
        "/opt/homebrew/lib/mono/msbuild/Current/bin",
        "/opt/homebrew/lib/mono/msbuild/15.0/bin",
        "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/msbuild/Current/bin",
        "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/msbuild/15.0/bin"
    ];
    string? directory = candidates.FirstOrDefault(
        static candidate =>
            File.Exists(Path.Join(candidate, "Microsoft.Build.dll")) &&
            File.Exists(Path.Join(
                candidate,
                "Roslyn",
                "Microsoft.CSharp.Core.targets")));
    return directory ?? throw new FileNotFoundException(
        "Mono is installed without the MSBuild and compiler-target layout required by Roslyn.");
}

static Task<string> RunPrivilegedAsync(
    string executablePath,
    IReadOnlyList<string> arguments) =>
    string.Equals(Environment.UserName, "root", StringComparison.Ordinal)
        ? RunCheckedAsync(executablePath, arguments)
        : RunCheckedAsync("sudo", ["--non-interactive", executablePath, .. arguments]);

static async Task<string> RunCheckedAsync(
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
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}: " +
            $"{standardError}{standardOutput}".Trim());
    }

    if (standardOutput.Length > 0)
    {
        await Console.Out.WriteAsync(standardOutput).ConfigureAwait(false);
        if (!standardOutput.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            await Console.Out.WriteLineAsync().ConfigureAwait(false);
        }
    }

    if (standardError.Length > 0)
    {
        await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
        if (!standardError.EndsWith(Environment.NewLine, StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync().ConfigureAwait(false);
        }
    }

    return standardOutput;
}
