#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs the latest stable VS Code extension test client and editor runtime.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-VsCode.cs " +
        "[--output <directory>] [--with-web-browsers]")
        .ConfigureAwait(false);
    return 0;
}

string? outputPath = null;
bool installWebBrowsers = false;
for (int index = 0; index < args.Length; index++)
{
    if (string.Equals(args[index], "--with-web-browsers", StringComparison.Ordinal))
    {
        installWebBrowsers = true;
        continue;
    }

    if (string.Equals(args[index], "--output", StringComparison.Ordinal) &&
        index + 1 < args.Length &&
        outputPath is null)
    {
        outputPath = args[++index];
        continue;
    }

    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-VsCode.cs " +
        "[--output <directory>] [--with-web-browsers]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        outputPath);
    string fixturePath = Path.Join(repositoryRoot, "tests", "vscode");
    (string npmExecutable, IReadOnlyList<string> npmPrefix) = ResolveNpmInvocation();
    await RunCheckedAsync(
        npmExecutable,
        [
            .. npmPrefix,
            "ci",
            "--ignore-scripts",
            "--prefix",
            fixturePath
        ],
        repositoryRoot).ConfigureAwait(false);
    if (installWebBrowsers)
    {
        string browserCachePath = Path.Join(toolsRoot, "playwright", "current");
        Directory.CreateDirectory(browserCachePath);
        await RunCheckedAsync(
            "node",
            [
                Path.Join(fixturePath, "node_modules", "playwright-core", "cli.js"),
                "install",
                "chromium",
                "firefox",
                "webkit"
            ],
            repositoryRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PLAYWRIGHT_BROWSERS_PATH"] = browserCachePath
            }).ConfigureAwait(false);
    }
    string extensionPath = Path.Join(repositoryRoot, "editors", "vscode");
    await RunCheckedAsync(
        npmExecutable,
        [
            .. npmPrefix,
            "ci",
            "--ignore-scripts",
            "--prefix",
            extensionPath
        ],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        npmExecutable,
        [
            .. npmPrefix,
            "run",
            installWebBrowsers ? "compile" : "compile:node",
            "--prefix",
            extensionPath
        ],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        npmExecutable,
        [
            .. npmPrefix,
            "run",
            installWebBrowsers ? "compile" : "compile:desktop",
            "--prefix",
            fixturePath
        ],
        repositoryRoot).ConfigureAwait(false);

    string cachePath = Path.Join(toolsRoot, "vscode", "stable");
    Directory.CreateDirectory(cachePath);
    string executablePath = (await RunCheckedAsync(
        "node",
        [Path.Join(fixturePath, "provision.mjs"), cachePath],
        repositoryRoot).ConfigureAwait(false)).Trim();
    if (!File.Exists(executablePath))
    {
        throw new InvalidDataException(
            "The VS Code stable-channel provisioner returned a missing executable: " +
            executablePath);
    }

    string runtimeExtensionPath = await ProvisionExtensionAsync(
        toolsRoot,
        "vscode-dotnet-runtime",
        "ms-dotnettools",
        "vscode-dotnet-runtime",
        targetPlatform: null).ConfigureAwait(false);
    string targetPlatform = ResolveVsCodeTargetPlatform();
    string csharpExtensionPath = await ProvisionExtensionAsync(
        toolsRoot,
        "vscode-csharp",
        "ms-dotnettools",
        "csharp",
        targetPlatform).ConfigureAwait(false);
    string csharpDevKitExtensionPath = await ProvisionExtensionAsync(
        toolsRoot,
        "vscode-csdevkit",
        "ms-dotnettools",
        "csdevkit",
        targetPlatform).ConfigureAwait(false);

    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(runtimeExtensionPath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(csharpExtensionPath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(csharpDevKitExtensionPath).ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    HttpRequestException or
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException or
    Win32Exception)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task<string> ProvisionExtensionAsync(
    string toolsRoot,
    string toolName,
    string publisher,
    string extensionName,
    string? targetPlatform)
{
    (string version, Uri source) = await ScriptSupport.ResolveLatestVsCodeExtensionAsync(
        publisher,
        extensionName,
        targetPlatform,
        CancellationToken.None).ConfigureAwait(false);
    string installationPath = Path.Join(
        toolsRoot,
        toolName,
        "current",
        targetPlatform ?? "all");
    string packagePath = Path.Join(
        installationPath,
        $"{publisher}.{extensionName}-{version}.vsix");
    if (File.Exists(packagePath))
    {
        return packagePath;
    }

    if (Directory.Exists(installationPath))
    {
        Directory.Delete(installationPath, recursive: true);
    }

    Directory.CreateDirectory(installationPath);
    await ScriptSupport.DownloadFileAsync(
        source,
        packagePath,
        CancellationToken.None).ConfigureAwait(false);
    return packagePath;
}

static string ResolveVsCodeTargetPlatform()
{
    Architecture architecture = RuntimeInformation.OSArchitecture;
    if (OperatingSystem.IsLinux() && architecture == Architecture.X64)
    {
        return File.Exists("/etc/alpine-release") ? "alpine-x64" : "linux-x64";
    }

    if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
    {
        return File.Exists("/etc/alpine-release") ? "alpine-arm64" : "linux-arm64";
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.X64)
    {
        return "darwin-x64";
    }

    if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
    {
        return "darwin-arm64";
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.X64)
    {
        return "win32-x64";
    }

    if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
    {
        return "win32-arm64";
    }

    throw new PlatformNotSupportedException(
        $"No VS Code extension package is available for " +
        $"{RuntimeInformation.OSDescription} {architecture}.");
}

static (string Executable, IReadOnlyList<string> Prefix) ResolveNpmInvocation()
{
    if (!OperatingSystem.IsWindows())
    {
        return ("npm", []);
    }

    string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    (string nodePath, string npmCliPath) = path
        .Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static directory =>
        {
            string normalizedDirectory = directory.Trim('"');
            return (
                NodePath: Path.Join(normalizedDirectory, "node.exe"),
                NpmCliPath: Path.Join(
                    normalizedDirectory,
                    "node_modules",
                    "npm",
                    "bin",
                    "npm-cli.js"));
        })
        .FirstOrDefault(static candidate =>
            File.Exists(candidate.NodePath) && File.Exists(candidate.NpmCliPath));
    if (nodePath is not null && npmCliPath is not null)
    {
        return (nodePath, [npmCliPath]);
    }

    throw new FileNotFoundException(
        "Node.js is installed without the npm CLI required to provision VS Code.");
}

static async Task<string> RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment = null)
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

    if (environment is not null)
    {
        foreach ((string name, string value) in environment)
        {
            startInfo.Environment[name] = value;
        }
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await standardOutputTask.ConfigureAwait(false);
    string error = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}: {error.Trim()}");
    }

    return output;
}
