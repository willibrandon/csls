#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;

const string Version = "25.07.1";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Downloads and verifies the pinned Helix editor release.").ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Helix.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--output", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-Helix.cs [--output <directory>]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = args.Length == 2
        ? Path.GetFullPath(args[1])
        : Path.Combine(repositoryRoot, "artifacts", "tools");
    (string platform, string assetName, string expectedSha256, string executableName) =
        SelectAsset();
    string installationPath = Path.Combine(toolsRoot, "helix", Version, platform);
    string executablePath = Path.Combine(installationPath, executableName);

    if (File.Exists(executablePath))
    {
        await VerifyVersionAsync(executablePath, CancellationToken.None).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
        return 0;
    }

    string stagingRoot = Path.Combine(
        toolsRoot,
        ".staging",
        $"helix-{Version}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingRoot);
    try
    {
        string archivePath = Path.Combine(stagingRoot, assetName);
        string extractionPath = Path.Combine(stagingRoot, "extracted");
        Directory.CreateDirectory(extractionPath);
        var source = new Uri(
            $"https://github.com/helix-editor/helix/releases/download/{Version}/{assetName}");

        await Console.Error.WriteLineAsync(
            $"Downloading Helix {Version} for {platform}...").ConfigureAwait(false);
        await ScriptSupport.DownloadVerifiedFileAsync(
            source,
            archivePath,
            expectedSha256,
            CancellationToken.None).ConfigureAwait(false);
        await ExtractArchiveAsync(
            archivePath,
            extractionPath,
            CancellationToken.None).ConfigureAwait(false);

        string sourceExecutablePath = Directory
            .EnumerateFiles(extractionPath, executableName, SearchOption.AllDirectories)
            .Single(path => string.Equals(
                Path.GetFileName(path),
                executableName,
                StringComparison.Ordinal));
        string sourceInstallationPath = Path.GetDirectoryName(sourceExecutablePath)
            ?? throw new InvalidDataException("The Helix archive has no installation root.");

        Directory.CreateDirectory(Path.GetDirectoryName(installationPath)!);
        if (Directory.Exists(installationPath))
        {
            Directory.Delete(installationPath, recursive: true);
        }

        Directory.Move(sourceInstallationPath, installationPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                executablePath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        }

        await VerifyVersionAsync(executablePath, CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
    }

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

static (string Platform, string AssetName, string Sha256, string ExecutableName) SelectAsset()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return (
            "linux-x64",
            "helix-25.07.1-x86_64-linux.tar.xz",
            "3f08e63ecd388fff657ad39722f88bb03dcf326f1f2da2700d99e1dc40ab2e8b",
            "hx");
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return (
            "linux-arm64",
            "helix-25.07.1-aarch64-linux.tar.xz",
            "ce23fa8d395e633e3e54c052012f11965d91d8d5c2bfa659685f50430b4f8175",
            "hx");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return (
            "osx-x64",
            "helix-25.07.1-x86_64-macos.tar.xz",
            "84dc32d617d28d32f4aa21e3aafac47bd715d1154aeb977697d4d60b887b7103",
            "hx");
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return (
            "osx-arm64",
            "helix-25.07.1-aarch64-macos.tar.xz",
            "00b1651b4fdbbe0a2ae981c8e76b858bd26a7c33f5b3583f3b6bb9137d54f1ff",
            "hx");
    }

    if (OperatingSystem.IsWindows() && architecture is Architecture.X64 or Architecture.Arm64)
    {
        return (
            "win-x64",
            "helix-25.07.1-x86_64-windows.zip",
            "5c8325ced8bacd8418d62706f669e96d9c3578a9237526e34d546900cbc049b6",
            "hx.exe");
    }

    throw new PlatformNotSupportedException(
        $"Helix {Version} has no release asset for {RuntimeInformation.OSDescription} {architecture}.");
}

static async Task ExtractArchiveAsync(
    string archivePath,
    string destinationPath,
    CancellationToken cancellationToken)
{
    if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
    {
        await ZipFile.ExtractToDirectoryAsync(
            archivePath,
            destinationPath,
            overwriteFiles: false,
            cancellationToken).ConfigureAwait(false);
        return;
    }

    string tarPath = Path.Combine(Path.GetDirectoryName(archivePath)!, "helix.tar");
    using FileStream compressedArchive = File.OpenRead(archivePath);
    using var decompressedArchive = new XZStream(compressedArchive);
    FileStream tarFile = new(
        tarPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 131_072,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    await using (tarFile.ConfigureAwait(false))
    {
        await decompressedArchive.CopyToAsync(tarFile, cancellationToken)
            .ConfigureAwait(false);
    }

    using IArchive archive = TarArchive.OpenArchive(tarPath);
    archive.WriteToDirectory(
        destinationPath,
        new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = false,
            PreserveFileTime = true
        });
}

static async Task VerifyVersionAsync(
    string executablePath,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executablePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("--version");
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("The provisioned Helix executable did not start.");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    string output = await standardOutputTask.ConfigureAwait(false) +
        await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0 || !output.Contains(Version, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Expected Helix {Version}, but '{executablePath} --version' returned " +
            $"exit code {process.ExitCode}: {output.Trim()}");
    }
}
