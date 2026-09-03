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
        "Prepares the VS Code test fixtures and latest stable editor toolset.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Provision-VsCode.cs " +
        "[--output <directory>] [--with-web-browsers] [--fixtures-only] " +
        "[--web-only] [--web-browser <chromium|firefox|webkit>] " +
        "[--without-dev-kit]")
        .ConfigureAwait(false);
    return 0;
}

string? outputPath = null;
var webBrowsers = new HashSet<string>(StringComparer.Ordinal);
bool fixturesOnly = false;
bool webOnly = false;
bool provisionDevKit = true;
for (int index = 0; index < args.Length; index++)
{
    if (string.Equals(args[index], "--with-web-browsers", StringComparison.Ordinal))
    {
        webBrowsers.UnionWith(["chromium", "firefox", "webkit"]);
        continue;
    }

    if (string.Equals(args[index], "--web-browser", StringComparison.Ordinal) &&
        index + 1 < args.Length &&
        args[index + 1] is "chromium" or "firefox" or "webkit")
    {
        webBrowsers.Add(args[++index]);
        continue;
    }

    if (string.Equals(args[index], "--fixtures-only", StringComparison.Ordinal))
    {
        fixturesOnly = true;
        continue;
    }

    if (string.Equals(args[index], "--web-only", StringComparison.Ordinal))
    {
        webOnly = true;
        continue;
    }

    if (string.Equals(args[index], "--without-dev-kit", StringComparison.Ordinal))
    {
        provisionDevKit = false;
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
        "[--output <directory>] [--with-web-browsers] [--fixtures-only] " +
        "[--web-only] [--web-browser <chromium|firefox|webkit>] " +
        "[--without-dev-kit]")
        .ConfigureAwait(false);
    return 2;
}

if (webOnly && webBrowsers.Count == 0)
{
    webBrowsers.UnionWith(["chromium", "firefox", "webkit"]);
}

bool installWebBrowsers = webBrowsers.Count > 0;

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string toolsRoot = ScriptSupport.ResolveToolsRoot(
        repositoryRoot,
        outputPath);
    string fixturePath = Path.Join(repositoryRoot, "tests", "vscode");
    string extensionPath = Path.Join(repositoryRoot, "editors", "vscode");
    (string npmExecutable, IReadOnlyList<string> npmPrefix) = ResolveNpmInvocation();
    Task<string> fixtureInstallTask = RunCheckedAsync(
        npmExecutable,
        [
            .. npmPrefix,
            "ci",
            "--ignore-scripts",
            "--no-audit",
            "--no-fund",
            "--prefer-offline",
            "--prefix",
            fixturePath
        ],
        repositoryRoot);
    Task<string>? extensionInstallTask = webOnly
        ? null
        : RunCheckedAsync(
            npmExecutable,
            [
                .. npmPrefix,
                "ci",
                "--ignore-scripts",
                "--no-audit",
                "--no-fund",
                "--prefer-offline",
                "--prefix",
                extensionPath
            ],
            repositoryRoot);
    await fixtureInstallTask.ConfigureAwait(false);
    if (extensionInstallTask is not null)
    {
        await extensionInstallTask.ConfigureAwait(false);
    }

    Task<string>? extensionCompileTask = webOnly
        ? null
        : RunCheckedAsync(
            npmExecutable,
            [
                .. npmPrefix,
                "run",
                installWebBrowsers ? "compile" : "compile:node",
                "--prefix",
                extensionPath
            ],
            repositoryRoot);
    Task<string> fixtureCompileTask = RunCheckedAsync(
        npmExecutable,
        [
            .. npmPrefix,
            "run",
            webOnly ? "compile:web" : installWebBrowsers ? "compile" : "compile:desktop",
            "--prefix",
            fixturePath
        ],
        repositoryRoot);
    if (fixturesOnly)
    {
        if (extensionCompileTask is not null)
        {
            await extensionCompileTask.ConfigureAwait(false);
        }
        await fixtureCompileTask.ConfigureAwait(false);
        await Console.Out.WriteLineAsync("Prepared the VS Code test fixtures.")
            .ConfigureAwait(false);
        return 0;
    }

    string? targetPlatform = webOnly ? null : ResolveVsCodeTargetPlatform();
    Task<string>? runtimeExtensionProvisionTask = webOnly
        ? null
        : ProvisionExtensionAsync(
            toolsRoot,
            "vscode-dotnet-runtime",
            "ms-dotnettools",
            "vscode-dotnet-runtime",
            targetPlatform: null);
    Task<string>? csharpExtensionProvisionTask = webOnly
        ? null
        : ProvisionExtensionAsync(
            toolsRoot,
            "vscode-csharp",
            "ms-dotnettools",
            "csharp",
            targetPlatform);
    Task<string>? csharpDevKitExtensionProvisionTask = webOnly || !provisionDevKit
        ? null
        : ProvisionExtensionAsync(
            toolsRoot,
            "vscode-csdevkit",
            "ms-dotnettools",
            "csdevkit",
            targetPlatform);
    Task<string>? browserInstallTask = null;
    if (installWebBrowsers)
    {
        string browserCachePath = Path.Join(toolsRoot, "playwright", "current");
        Directory.CreateDirectory(browserCachePath);
        browserInstallTask = RunCheckedAsync(
            "node",
            [
                Path.Join(fixturePath, "node_modules", "playwright-core", "cli.js"),
                "install",
                .. webBrowsers.Order(StringComparer.Ordinal)
            ],
            repositoryRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PLAYWRIGHT_BROWSERS_PATH"] = browserCachePath
            });
    }

    string desktopCachePath = Path.Join(toolsRoot, "vscode", "stable");
    string webCachePath = Path.Join(toolsRoot, "vscode-web", "stable");
    List<string> provisionArguments = [Path.Join(fixturePath, "provision.mjs")];
    if (webOnly)
    {
        Directory.CreateDirectory(webCachePath);
        provisionArguments.Add("--web-only");
        provisionArguments.Add(webCachePath);
    }
    else
    {
        Directory.CreateDirectory(desktopCachePath);
        provisionArguments.Add(desktopCachePath);
        if (installWebBrowsers)
        {
            Directory.CreateDirectory(webCachePath);
            provisionArguments.Add(webCachePath);
        }
    }

    Task<string> provisionTask = RunCheckedAsync(
        "node",
        provisionArguments,
        repositoryRoot);
    List<Task> provisioningTasks =
    [
        fixtureCompileTask,
        provisionTask
    ];
    if (extensionCompileTask is not null)
    {
        provisioningTasks.Add(extensionCompileTask);
    }
    if (runtimeExtensionProvisionTask is not null)
    {
        provisioningTasks.Add(runtimeExtensionProvisionTask);
        provisioningTasks.Add(csharpExtensionProvisionTask!);
        if (csharpDevKitExtensionProvisionTask is not null)
        {
            provisioningTasks.Add(csharpDevKitExtensionProvisionTask);
        }
    }
    if (browserInstallTask is not null)
    {
        provisioningTasks.Add(browserInstallTask);
    }

    await Task.WhenAll(provisioningTasks).ConfigureAwait(false);
    string provisionOutput = await provisionTask.ConfigureAwait(false);
    string executablePath = provisionOutput
        .Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault()
        ?? string.Empty;
    if (webOnly ? !Directory.Exists(executablePath) : !File.Exists(executablePath))
    {
        throw new InvalidDataException(
            "The VS Code stable-channel provisioner returned a missing executable: " +
            executablePath);
    }

    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
    if (runtimeExtensionProvisionTask is not null)
    {
        await Console.Out.WriteLineAsync(
            await runtimeExtensionProvisionTask.ConfigureAwait(false)).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            await csharpExtensionProvisionTask!.ConfigureAwait(false)).ConfigureAwait(false);
        if (csharpDevKitExtensionProvisionTask is not null)
        {
            await Console.Out.WriteLineAsync(
                await csharpDevKitExtensionProvisionTask.ConfigureAwait(false))
                .ConfigureAwait(false);
        }
    }
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
    string command = string.Join(' ', arguments.Prepend(executablePath));
    await Console.Error.WriteLineAsync($"Starting {command}").ConfigureAwait(false);
    long startedTimestamp = Stopwatch.GetTimestamp();
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
    await Console.Error.WriteLineAsync(
        $"Completed {command} in {Stopwatch.GetElapsedTime(startedTimestamp)}")
        .ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}: {error.Trim()}");
    }

    return output;
}
