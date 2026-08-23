#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

const string Version = "30.2";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads, builds when needed, and verifies the pinned GNU Emacs Eglot test oracle.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Emacs.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Emacs.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(repositoryRoot, "artifacts", "tools");
    string platform = SelectPlatform();
    string executablePath = OperatingSystem.IsWindows()
        ? await ProvisionWindowsAsync(toolsRoot, platform).ConfigureAwait(false)
        : await ProvisionUnixAsync(toolsRoot, platform).ConfigureAwait(false);

    await VerifyEglotAsync(executablePath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
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

static string SelectPlatform()
{
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"GNU Emacs provisioning does not support {RuntimeInformation.OSArchitecture}.")
    };
    if (OperatingSystem.IsWindows())
    {
        return $"win-{architecture}";
    }

    if (OperatingSystem.IsMacOS())
    {
        return $"osx-{architecture}";
    }

    if (OperatingSystem.IsLinux())
    {
        string platform = File.Exists("/etc/alpine-release") ? "linux-musl" : "linux";
        return $"{platform}-{architecture}";
    }

    throw new PlatformNotSupportedException(
        $"GNU Emacs provisioning does not support {RuntimeInformation.OSDescription}.");
}

static Task<string> ProvisionWindowsAsync(string toolsRoot, string platform) =>
    ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "emacs",
        Version,
        platform,
        new Uri("https://ftp.gnu.org/gnu/windows/emacs/emacs-30/emacs-30.2.zip"),
        "emacs-30.2.zip",
        "414d3a1a21147af257ebd98bdd15976fdcb5ed0563f6de89f76d4a4b5dad9c72",
        "emacs.exe",
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: $"GNU Emacs {Version}",
        CancellationToken.None);

static async Task<string> ProvisionUnixAsync(string toolsRoot, string platform)
{
    string installationPath = Path.Combine(toolsRoot, "emacs", Version, platform);
    string executablePath = Path.Combine(installationPath, "bin", "emacs");
    if (File.Exists(executablePath))
    {
        await VerifyEglotAsync(executablePath).ConfigureAwait(false);
        return executablePath;
    }

    string stagingRoot = Path.Combine(
        toolsRoot,
        ".staging",
        $"emacs-{Version}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingRoot);
    try
    {
        const string assetName = "emacs-30.2.tar.xz";
        string archivePath = Path.Combine(stagingRoot, assetName);
        string extractionPath = Path.Combine(stagingRoot, "source");
        Directory.CreateDirectory(extractionPath);
        await Console.Error.WriteLineAsync(
            $"Downloading GNU Emacs {Version} source for {platform}...").ConfigureAwait(false);
        await ScriptSupport.DownloadVerifiedFileAsync(
            new Uri($"https://ftp.gnu.org/gnu/emacs/{assetName}"),
            archivePath,
            "b3f36f18a6dd2715713370166257de2fae01f9d38cfe878ced9b1e6ded5befd9",
            CancellationToken.None).ConfigureAwait(false);
        await ScriptSupport.ExtractArchiveAsync(
            archivePath,
            extractionPath,
            CancellationToken.None).ConfigureAwait(false);
        string sourcePath = Directory
            .EnumerateDirectories(extractionPath, $"emacs-{Version}", SearchOption.TopDirectoryOnly)
            .Single();
        string configurePath = Path.Combine(sourcePath, "configure");
        ScriptSupport.EnsureExecutable(configurePath);
        await Console.Error.WriteLineAsync(
            $"Building terminal-only GNU Emacs {Version} for {platform}...").ConfigureAwait(false);
        await RunCheckedAsync(
            configurePath,
            [
                $"--prefix={installationPath}",
                "--disable-build-details",
                "--without-cairo",
                "--without-compress-install",
                "--without-dbus",
                "--without-gif",
                "--without-gpm",
                "--without-gsettings",
                "--without-gnutls",
                "--without-harfbuzz",
                "--without-jpeg",
                "--without-lcms2",
                "--without-libotf",
                "--without-libsystemd",
                "--without-m17n-flt",
                "--without-modules",
                "--without-native-compilation",
                "--without-ns",
                "--without-png",
                "--without-pop",
                "--without-rsvg",
                "--without-selinux",
                "--without-sound",
                "--without-sqlite3",
                "--without-tiff",
                "--without-tree-sitter",
                "--without-webp",
                "--without-x",
                "--without-xml2",
                "--without-xpm"
            ],
            sourcePath).ConfigureAwait(false);
        await RunCheckedAsync(
            "make",
            ["-j", Math.Min(Environment.ProcessorCount, 8).ToString(CultureInfo.InvariantCulture)],
            sourcePath).ConfigureAwait(false);

        string destinationRoot = Path.Combine(stagingRoot, "destination");
        Directory.CreateDirectory(destinationRoot);
        await RunCheckedAsync(
            "make",
            ["install", $"DESTDIR={destinationRoot}"],
            sourcePath).ConfigureAwait(false);
        string stagedInstallationPath = Path.Combine(
            destinationRoot,
            installationPath.TrimStart(Path.DirectorySeparatorChar));
        if (!Directory.Exists(stagedInstallationPath))
        {
            throw new InvalidDataException(
                $"GNU Emacs did not install into the expected path: {stagedInstallationPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(installationPath)!);
        if (Directory.Exists(installationPath))
        {
            Directory.Delete(installationPath, recursive: true);
        }

        Directory.Move(stagedInstallationPath, installationPath);
        ScriptSupport.EnsureExecutable(executablePath);
        return executablePath;
    }
    finally
    {
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
    }
}

static async Task VerifyEglotAsync(string executablePath)
{
    string output = await RunCheckedAsync(
        executablePath,
        [
            "--batch",
            "-Q",
            "--eval",
            "(progn (require 'eglot) " +
            "(princ (format \"GNU Emacs %s; Eglot ready\" emacs-version)))"
        ],
        Directory.GetCurrentDirectory()).ConfigureAwait(false);
    string expected = $"GNU Emacs {Version}; Eglot ready";
    if (!output.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Expected '{expected}' from {executablePath}, but received: {output.Trim()}");
    }
}

static async Task<string> RunCheckedAsync(
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
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}:" +
            $"{Environment.NewLine}{standardOutput}{standardError}");
    }

    return standardOutput + standardError;
}
