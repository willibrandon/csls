#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

const string MonoRepositoryPath = "/etc/apt/sources.list.d/mono-official-stable.list";
const string MonoPackageBaseUrl = "https://download.mono-project.com/repo/debian/";
const string MsBuildPackageFileName =
    "msbuild_16.10.1+xamarinxplat.2021.05.26.14.00-0xamarin2+debian10b1_all.deb";
const string MsBuildPackageSha256 =
    "a177610be806c44877169a3fef7f15ffc65e2df7bdbe11b532eb6cc8f92b4c3c";
const string MsBuildSdkResolverPackageFileName =
    "msbuild-sdkresolver_16.10.1+xamarinxplat.2021.05.26.14.00-0xamarin2+debian10b1_all.deb";
const string MsBuildSdkResolverPackageSha256 =
    "8988037618086059b43fb479335c63ffc24ceda5f0d5bdf5f0890081c6abe5ad";
const string MsBuildLibHostFxrVersion =
    "3.0.0.2019.04.16.02.13-0xamarin4+debian10b1";
const string MonoRoslynPackageFileName =
    "mono-roslyn_6.12.0.200-0xamarin2+debian10b1_all.deb";
const string MonoRoslynPackageSha256 =
    "3edd5398c29528ffd8ac477b6c8c577ee891d17c927fc011962224ba81d0b3eb";

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
    (string Architecture, string Sha256) hostFxr = RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 =>
            ("amd64", "5922cdf2a04a4bd4bcd57d765d4182600fc449866d5b4d50784c0c5dabc08043"),
        Architecture.Arm64 =>
            ("arm64", "f53b770eca70d798632350bf3ebafbdf265e58a984d3ffbb29f83cc1fa6e4887"),
        Architecture.X86 =>
            ("i386", "415c81d47b7cad5e9b73f6ed2ec0f5d470b6db947791e02526140c5155b6feb6"),
        Architecture.Arm =>
            ("armhf", "003295a585830a9baaa360c8dedc29998e3d4f11d947c97b318f363d9d704166"),
        _ => throw new PlatformNotSupportedException(
            $"Mono MSBuild is unavailable for {RuntimeInformation.ProcessArchitecture}.")
    };
    string hostFxrPackageFileName =
        $"msbuild-libhostfxr_{MsBuildLibHostFxrVersion}_{hostFxr.Architecture}.deb";
    (string FileName, string RelativePath, string Sha256)[] packages =
    [
        (
            MsBuildPackageFileName,
            $"pool/main/m/msbuild/{MsBuildPackageFileName}",
            MsBuildPackageSha256),
        (
            MsBuildSdkResolverPackageFileName,
            $"pool/main/m/msbuild/{MsBuildSdkResolverPackageFileName}",
            MsBuildSdkResolverPackageSha256),
        (
            hostFxrPackageFileName,
            $"pool/main/c/core-setup/{hostFxrPackageFileName}",
            hostFxr.Sha256),
        (
            MonoRoslynPackageFileName,
            $"pool/main/m/mono/{MonoRoslynPackageFileName}",
            MonoRoslynPackageSha256)
    ];

    string cacheDirectory = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "csls",
        "legacy-build-host");
    Directory.CreateDirectory(cacheDirectory);
    using var httpClient = new HttpClient();
    var packagePaths = new List<string>(packages.Length);
    foreach ((string fileName, string relativePath, string sha256) in packages)
    {
        string packagePath = Path.Join(cacheDirectory, fileName);
        if (!File.Exists(packagePath) ||
            !await HasSha256Async(packagePath, sha256).ConfigureAwait(false))
        {
            string partialPath = packagePath + ".partial";
            File.Delete(partialPath);
            try
            {
                using Stream source = await httpClient.GetStreamAsync(
                    new Uri(MonoPackageBaseUrl + relativePath)).ConfigureAwait(false);
                using (var destination = new FileStream(
                    partialPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await source.CopyToAsync(destination).ConfigureAwait(false);
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

static async Task<string> CreateCompatibleMonoRoslynPackageAsync(string sourcePackagePath)
{
    string cacheDirectory = Path.GetDirectoryName(sourcePackagePath)
        ?? throw new InvalidDataException(
            $"The Mono Roslyn package has no parent directory: {sourcePackagePath}");
    string packageRoot = Path.Join(cacheDirectory, "csls-mono-roslyn-package");
    string outputPackagePath = Path.Join(
        cacheDirectory,
        "csls-mono-roslyn_6.12.0.200-1_all.deb");
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

    const string controlText = """
        Package: csls-mono-roslyn
        Version: 6.12.0.200-1
        Architecture: all
        Maintainer: csls contributors <noreply@localhost>
        Depends: mono-runtime (>= 3.0~), mono-devel, msbuild (>= 1:16.10)
        Provides: mono-roslyn (= 6.12.0.200)
        Conflicts: mono-roslyn
        Replaces: mono-roslyn
        Section: devel
        Priority: optional
        Homepage: https://www.mono-project.com/
        Description: Mono Roslyn compiler payload for csls legacy workspaces
         Pinned Mono compiler targets required by Roslyn's Mono MSBuild host.

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
