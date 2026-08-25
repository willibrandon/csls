#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;

const string MonoSigningKeyFingerprint = "3FA7E0328081BFF6A14DA29AA6A19B38D3D831EF";
const string MonoSigningKeySha256 =
    "34cde340f7208396329877f6a19b25b5e2f74fd414039d800c63375ab78f6b17";
const string MonoRepositoryPath = "/etc/apt/sources.list.d/mono-official-stable.list";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs and verifies the platform build host used for legacy .NET Framework projects.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-LegacyBuildHost.cs")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-LegacyBuildHost.cs")
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
    string repository;
    if (string.Equals(identifier, "ubuntu", StringComparison.OrdinalIgnoreCase))
    {
        repository =
            "deb [signed-by=/usr/share/keyrings/mono-official-archive-keyring.gpg] " +
            "https://download.mono-project.com/repo/ubuntu stable-focal main";
    }
    else if (string.Equals(identifier, "debian", StringComparison.OrdinalIgnoreCase))
    {
        repository =
            "deb [signed-by=/usr/share/keyrings/mono-official-archive-keyring.gpg] " +
            "https://download.mono-project.com/repo/debian stable-buster main";
    }
    else
    {
        throw new PlatformNotSupportedException(
            $"Automatic Mono provisioning does not support Linux distribution '{identifier}'.");
    }
    await RunPrivilegedAsync("rm", ["--force", MonoRepositoryPath]).ConfigureAwait(false);
    await RunPrivilegedAsync("apt-get", ["update"]).ConfigureAwait(false);
    await RunPrivilegedAsync(
        "apt-get",
        ["install", "--yes", "--no-install-recommends", "ca-certificates", "gnupg"])
        .ConfigureAwait(false);

    string stagingDirectory = Path.Join(
        Path.GetTempPath(),
        $"csls-mono-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingDirectory);
    try
    {
        string signingKeyPath = Path.Join(stagingDirectory, "xamarin.gpg");
        await ScriptSupport.DownloadVerifiedFileAsync(
            new Uri("https://download.mono-project.com/repo/xamarin.gpg"),
            signingKeyPath,
            MonoSigningKeySha256,
            CancellationToken.None).ConfigureAwait(false);
        string keyDetails = await RunCheckedAsync(
            "gpg",
            ["--batch", "--show-keys", "--with-colons", signingKeyPath])
            .ConfigureAwait(false);
        if (!keyDetails.Contains(MonoSigningKeyFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Mono repository signing key has an unexpected fingerprint.");
        }

        string dearmoredSigningKeyPath = Path.Join(stagingDirectory, "xamarin-keyring.gpg");
        await RunCheckedAsync(
            "gpg",
            [
                "--batch",
                "--yes",
                "--dearmor",
                "--output",
                dearmoredSigningKeyPath,
                signingKeyPath
            ]).ConfigureAwait(false);

        string repositoryPath = Path.Join(stagingDirectory, "mono-official-stable.list");
        await File.WriteAllTextAsync(
            repositoryPath,
            repository + Environment.NewLine).ConfigureAwait(false);
        await RunPrivilegedAsync(
            "install",
            [
                "--mode",
                "0644",
                dearmoredSigningKeyPath,
                "/usr/share/keyrings/mono-official-archive-keyring.gpg"
            ]).ConfigureAwait(false);
        await RunPrivilegedAsync(
            "install",
            [
                "--mode",
                "0644",
                repositoryPath,
                MonoRepositoryPath
            ]).ConfigureAwait(false);
    }
    finally
    {
        Directory.Delete(stagingDirectory, recursive: true);
    }

    await RunPrivilegedAsync("apt-get", ["update"]).ConfigureAwait(false);
    await RunPrivilegedAsync(
        "apt-get",
        ["install", "--yes", "--no-install-recommends", "mono-complete"])
        .ConfigureAwait(false);
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
    string msBuildPath = FindMonoMSBuildCommand();
    string msBuildVersion = (await RunCheckedAsync(
        msBuildPath,
        ["-version", "-nologo"]).ConfigureAwait(false)).Trim();
    return $"{monoVersion}; Mono MSBuild {msBuildVersion} at {msBuildPath}";
}

static string FindMonoMSBuildCommand()
{
    string? path = Environment.GetEnvironmentVariable("PATH");
    if (path is not null)
    {
        foreach (string directory in path.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string msBuildPath = Path.Join(directory, "msbuild");
            if (File.Exists(msBuildPath))
            {
                return msBuildPath;
            }
        }
    }

    throw new FileNotFoundException(
        "Mono is installed without the MSBuild host required by Roslyn.");
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

    await Console.Out.WriteAsync(standardOutput).ConfigureAwait(false);
    await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
    return standardOutput;
}
