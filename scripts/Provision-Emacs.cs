#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Finds or provisions and verifies a GNU Emacs Eglot test oracle.")
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
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        args.Length == 2 ? args[1] : null);
    string platform = SelectPlatform();
    (string ExecutablePath, string Version)? installedEmacs =
        await TryProvisionInstalledUnixAsync(toolsRoot, platform).ConfigureAwait(false);
    string executablePath;
    string version;
    if (installedEmacs is { } installed)
    {
        executablePath = installed.ExecutablePath;
        version = installed.Version;
    }
    else
    {
        (version, Uri source) = await ResolveLatestReleaseAsync().ConfigureAwait(false);
        executablePath = OperatingSystem.IsWindows()
            ? await ProvisionWindowsAsync(toolsRoot, platform, version).ConfigureAwait(false)
            : await ProvisionUnixAsync(toolsRoot, platform, version, source).ConfigureAwait(false);
    }

    await VerifyEglotAsync(executablePath, version).ConfigureAwait(false);
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

static Task<string> ProvisionWindowsAsync(
    string toolsRoot,
    string platform,
    string version) =>
    ScriptSupport.ProvisionArchiveToolAsync(
        toolsRoot,
        "emacs",
        version,
        platform,
        new Uri(
            $"https://mirrors.kernel.org/gnu/windows/emacs/emacs-{Version.Parse(version).Major}/" +
            $"emacs-{version}.zip"),
        $"emacs-{version}.zip",
        null,
        "emacs.exe",
        installationRootLevels: 1,
        versionArguments: ["--version"],
        expectedVersionText: $"GNU Emacs {version}",
        CancellationToken.None);

static async Task<(string ExecutablePath, string Version)?> TryProvisionInstalledUnixAsync(
    string toolsRoot,
    string platform)
{
    if (OperatingSystem.IsWindows())
    {
        return null;
    }

    string? installedExecutable = ResolveExecutableOnPath("emacs");
    if (installedExecutable is null)
    {
        return null;
    }

    string version;
    try
    {
        version = (await RunCheckedAsync(
            installedExecutable,
            [
                "--batch",
                "-Q",
                "--eval",
                "(progn (require 'eglot) (princ emacs-version))"
            ],
            Directory.GetCurrentDirectory()).ConfigureAwait(false)).Trim();
    }
    catch (InvalidOperationException)
    {
        return null;
    }

    if (!Version.TryParse(version, out _))
    {
        return null;
    }

    string executablePath = Path.Join(
        toolsRoot,
        "emacs",
        version,
        platform,
        "bin",
        "emacs");
    Directory.CreateDirectory(Path.GetDirectoryName(executablePath)!);
    if (!File.Exists(executablePath))
    {
        File.CreateSymbolicLink(executablePath, installedExecutable);
    }

    return (executablePath, version);
}

static string? ResolveExecutableOnPath(string executableName)
{
    string? path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    return path
        .Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(directory => Path.Join(directory, executableName))
        .FirstOrDefault(File.Exists);
}

static async Task<string> ProvisionUnixAsync(
    string toolsRoot,
    string platform,
    string version,
    Uri source)
{
    string installationPath = Path.Join(toolsRoot, "emacs", version, platform);
    string executablePath = Path.Join(installationPath, "bin", "emacs");
    if (File.Exists(executablePath))
    {
        await VerifyEglotAsync(executablePath, version).ConfigureAwait(false);
        return executablePath;
    }

    string stagingRoot = Path.Join(
        toolsRoot,
        ".staging",
        $"emacs-{version}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingRoot);
    try
    {
        string assetName = Path.GetFileName(source.LocalPath);
        string archivePath = Path.Join(stagingRoot, assetName);
        string extractionPath = Path.Join(stagingRoot, "source");
        Directory.CreateDirectory(extractionPath);
        await Console.Error.WriteLineAsync(
            $"Downloading GNU Emacs {version} source for {platform}...").ConfigureAwait(false);
        await ScriptSupport.DownloadFileAsync(
            source,
            archivePath,
            CancellationToken.None).ConfigureAwait(false);
        await ScriptSupport.ExtractArchiveAsync(
            archivePath,
            extractionPath,
            CancellationToken.None).ConfigureAwait(false);
        string sourcePath = Directory
            .EnumerateDirectories(
                extractionPath,
                $"emacs-{version}",
                SearchOption.TopDirectoryOnly)
            .Single();
        string configurePath = Path.Join(sourcePath, "configure");
        ScriptSupport.EnsureExecutable(configurePath);
        await Console.Error.WriteLineAsync(
            $"Building terminal-only GNU Emacs {version} for {platform}...")
            .ConfigureAwait(false);
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
            ["-j", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)],
            sourcePath).ConfigureAwait(false);

        string destinationRoot = Path.Join(stagingRoot, "destination");
        Directory.CreateDirectory(destinationRoot);
        await RunCheckedAsync(
            "make",
            ["install", $"DESTDIR={destinationRoot}"],
            sourcePath).ConfigureAwait(false);
        string stagedInstallationPath = Path.Join(
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

static async Task VerifyEglotAsync(string executablePath, string version)
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
    string expected = $"GNU Emacs {version}; Eglot ready";
    if (!output.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Expected '{expected}' from {executablePath}, but received: {output.Trim()}");
    }
}

static async Task<(string Version, Uri Source)> ResolveLatestReleaseAsync()
{
    var sourceDirectory = new Uri("https://mirrors.kernel.org/gnu/emacs/");
    using var handler = new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        CheckCertificateRevocationList = !OperatingSystem.IsMacOS()
    };
    using var client = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromMinutes(2)
    };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-tool-provisioner");
    string index = await client.GetStringAsync(sourceDirectory).ConfigureAwait(false);
    const string prefix = "href=\"emacs-";
    const string suffix = ".tar.xz";
    var releases = new List<(string Text, Version Parsed)>();
    int searchIndex = 0;
    while (true)
    {
        int prefixIndex = index.IndexOf(prefix, searchIndex, StringComparison.Ordinal);
        if (prefixIndex < 0)
        {
            break;
        }

        int versionIndex = prefixIndex + prefix.Length;
        int quoteIndex = index.IndexOf('"', versionIndex);
        if (quoteIndex < 0)
        {
            break;
        }

        string assetName = index[versionIndex..quoteIndex];
        if (assetName.EndsWith(suffix, StringComparison.Ordinal))
        {
            string candidate = assetName[..^suffix.Length];
            if (Version.TryParse(candidate, out Version? parsed) &&
                candidate.IndexOf('.', StringComparison.Ordinal) ==
                candidate.LastIndexOf('.'))
            {
                releases.Add((candidate, parsed));
            }
        }

        searchIndex = quoteIndex + 1;
    }

    string version = releases
        .DistinctBy(static release => release.Text, StringComparer.Ordinal)
        .OrderByDescending(static release => release.Parsed)
        .Select(static release => release.Text)
        .FirstOrDefault()
        ?? throw new InvalidDataException(
            $"No stable GNU Emacs source release was found at {sourceDirectory}.");
    return (version, new Uri(sourceDirectory, $"emacs-{version}.tar.xz"));
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
