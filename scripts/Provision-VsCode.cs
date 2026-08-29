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

const string Version = "1.135.0";
const string NpmVersion = "12.0.2";
const string DotNetRuntimeExtensionVersion = "3.1.0";
const string DotNetRuntimeExtensionSha256 =
    "8e675ffe5f3674430d63e28d2dc05ab40f36c8494e9549e79d3995d721b13f5a";
const string CSharpExtensionVersion = "2.140.9";
const string CSharpDevKitExtensionVersion = "3.20.199";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs the pinned VS Code extension test client and editor runtime.")
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
    (string npxExecutable, IReadOnlyList<string> npxPrefix) = ResolveNpxInvocation();
    await RunCheckedAsync(
        npxExecutable,
        [
            .. npxPrefix,
            "--yes",
            $"npm@{NpmVersion}",
            "ci",
            "--ignore-scripts",
            "--prefix",
            fixturePath
        ],
        repositoryRoot).ConfigureAwait(false);
    if (installWebBrowsers)
    {
        string browserCachePath = Path.Join(toolsRoot, "playwright", "1.62.1");
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
        npxExecutable,
        [
            .. npxPrefix,
            "--yes",
            $"npm@{NpmVersion}",
            "ci",
            "--ignore-scripts",
            "--prefix",
            extensionPath
        ],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        npxExecutable,
        [
            .. npxPrefix,
            "--yes",
            $"npm@{NpmVersion}",
            "run",
            installWebBrowsers ? "compile" : "compile:node",
            "--prefix",
            extensionPath
        ],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        npxExecutable,
        [
            .. npxPrefix,
            "--yes",
            $"npm@{NpmVersion}",
            "run",
            installWebBrowsers ? "compile" : "compile:desktop",
            "--prefix",
            fixturePath
        ],
        repositoryRoot).ConfigureAwait(false);

    string cachePath = Path.Join(toolsRoot, "vscode", Version);
    Directory.CreateDirectory(cachePath);
    string executablePath = (await RunVsCodeProvisionerAsync(
        "node",
        [Path.Join(fixturePath, "provision.mjs"), cachePath],
        repositoryRoot).ConfigureAwait(false)).Trim();
    if (!File.Exists(executablePath))
    {
        throw new InvalidDataException(
            $"The VS Code {Version} provisioner returned a missing executable: " +
            executablePath);
    }

    string runtimeExtensionPath = await ProvisionExtensionAsync(
        toolsRoot,
        "vscode-dotnet-runtime",
        "ms-dotnettools",
        "vscode-dotnet-runtime",
        DotNetRuntimeExtensionVersion,
        DotNetRuntimeExtensionSha256,
        targetPlatform: null).ConfigureAwait(false);
    string targetPlatform = ResolveVsCodeTargetPlatform();
    string csharpExtensionPath = await ProvisionExtensionAsync(
        toolsRoot,
        "vscode-csharp",
        "ms-dotnettools",
        "csharp",
        CSharpExtensionVersion,
        ResolveExtensionSha256("csharp", targetPlatform),
        targetPlatform).ConfigureAwait(false);
    string csharpDevKitExtensionPath = await ProvisionExtensionAsync(
        toolsRoot,
        "vscode-csdevkit",
        "ms-dotnettools",
        "csdevkit",
        CSharpDevKitExtensionVersion,
        ResolveExtensionSha256("csdevkit", targetPlatform),
        targetPlatform).ConfigureAwait(false);

    await Console.Out.WriteLineAsync(executablePath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(runtimeExtensionPath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(csharpExtensionPath).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(csharpDevKitExtensionPath).ConfigureAwait(false);
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

static async Task<string> ProvisionExtensionAsync(
    string toolsRoot,
    string toolName,
    string publisher,
    string extensionName,
    string version,
    string expectedSha256,
    string? targetPlatform)
{
    string installationPath = Path.Join(
        toolsRoot,
        toolName,
        version,
        targetPlatform ?? "all");
    string packagePath = Path.Join(
        installationPath,
        $"{publisher}.{extensionName}-{version}.vsix");
    if (File.Exists(packagePath))
    {
        string actualSha256 = await ScriptSupport.ComputeSha256Async(
            packagePath,
            CancellationToken.None).ConfigureAwait(false);
        if (string.Equals(
            actualSha256,
            expectedSha256,
            StringComparison.OrdinalIgnoreCase))
        {
            return packagePath;
        }

        File.Delete(packagePath);
    }

    Directory.CreateDirectory(installationPath);
    string sourceText =
        $"https://{publisher}.gallery.vsassets.io/_apis/public/gallery/publisher/" +
        $"{publisher}/extension/{extensionName}/{version}/assetbyname/" +
        "Microsoft.VisualStudio.Services.VSIXPackage";
    if (targetPlatform is not null)
    {
        sourceText += $"?targetPlatform={Uri.EscapeDataString(targetPlatform)}";
    }

    var source = new Uri(sourceText);
    await ScriptSupport.DownloadVerifiedFileAsync(
        source,
        packagePath,
        expectedSha256,
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

static string ResolveExtensionSha256(string extensionName, string targetPlatform) =>
    (extensionName, targetPlatform) switch
    {
        ("csharp", "alpine-arm64") =>
            "85c63d9bd20fd557e58f517f4dda7cacc100f8b7cd179b01b7a2d7eced6718ab",
        ("csharp", "alpine-x64") =>
            "febec0ec3d0cb92a50be94698a3d8edb9b1cb93d202c3d9aa471976614bd5512",
        ("csharp", "darwin-arm64") =>
            "0fa50f6413e46f14d56ac2870cebc2c6a6395e6099daa7cd1a60e2aadd4cd2f6",
        ("csharp", "darwin-x64") =>
            "487086420b7064f687da5af24fc0396b2fa7c24949ba0bb4440fe77574990626",
        ("csharp", "linux-arm64") =>
            "6c862668b839f7811d2aca3a3181a30606adfc3b08d7f742d4b0a550719148fb",
        ("csharp", "linux-x64") =>
            "0efeeefbf154814cd7dadbe2d3e3a4f7f526baff38c4f1045205b94f3f8e8336",
        ("csharp", "win32-arm64") =>
            "7c872933e8a5d74b1f4aed4ac8bb1ccd33665441e41c92a7038567d5f5160e65",
        ("csharp", "win32-x64") =>
            "ed7a3ca7775b0afa0c7c8e51e203eba0ca5e7366969509a3bbdd707888b8536a",
        ("csdevkit", "alpine-arm64") =>
            "1c36ce56283004b49ba582b67bc33c2c209b18a136d23836d8a317417662d7e2",
        ("csdevkit", "alpine-x64") =>
            "e2cd0a91faa18332fb76f8b62138dffc2758b50bb680bb1ec48fe68ab4c7839a",
        ("csdevkit", "darwin-arm64") =>
            "ad69e17c349eaa9cc5281c4bb7aab757a2f1c378e3522d7500f0f31333d53671",
        ("csdevkit", "darwin-x64") =>
            "d2bb0b2c564a4c49a4224cee3a2e0845db127f9bcc7ae3f0d83698f2a939b4e3",
        ("csdevkit", "linux-arm64") =>
            "ed8b5992335fd7a935533ab5f4f2c19fb5334778e774bba1c07d346c174dfcc7",
        ("csdevkit", "linux-x64") =>
            "4d5ae2f3fd6aad529516246cff982583bb27250cb961a7022854ba292217f8f7",
        ("csdevkit", "win32-arm64") =>
            "7078659dda0c719bd1ce67490b1eb24f5f7ff4016e2bbd52b42e38bca9d008b0",
        ("csdevkit", "win32-x64") =>
            "9060b6ef5b290d3718a6ebe401ba0a2118bb47716193b4edbec32cc5d85d8dad",
        _ => throw new PlatformNotSupportedException(
            $"No verified {extensionName} package is available for {targetPlatform}.")
    };

static (string Executable, IReadOnlyList<string> Prefix) ResolveNpxInvocation()
{
    if (!OperatingSystem.IsWindows())
    {
        return ("npx", []);
    }

    string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    (string nodePath, string npxCliPath) = path
        .Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static directory =>
        {
            string normalizedDirectory = directory.Trim('"');
            return (
                NodePath: Path.Join(normalizedDirectory, "node.exe"),
                NpxCliPath: Path.Join(
                    normalizedDirectory,
                    "node_modules",
                    "npm",
                    "bin",
                    "npx-cli.js"));
        })
        .FirstOrDefault(static candidate =>
            File.Exists(candidate.NodePath) && File.Exists(candidate.NpxCliPath));
    if (nodePath is not null && npxCliPath is not null)
    {
        return (nodePath, [npxCliPath]);
    }

    throw new FileNotFoundException(
        "Node.js is installed without the npm npx CLI required to provision VS Code.");
}

static async Task<string> RunVsCodeProvisionerAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory)
{
    const int maximumAttempts = 3;
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            return await RunCheckedAsync(
                executablePath,
                arguments,
                workingDirectory).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (attempt < maximumAttempts)
        {
            await Console.Error.WriteLineAsync(
                $"VS Code download attempt {attempt} failed: " +
                exception.GetBaseException().Message).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(attempt * 2)).ConfigureAwait(false);
        }
    }
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
