#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true

using System.Diagnostics;

const string TreeSitterCliVersion = "0.26.12";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs the Linux display and input packages used by graphical editor tests.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Install-GraphicalEditorTestPrerequisites.cs " +
        "[--with-web-browsers]")
        .ConfigureAwait(false);
    return 0;
}

bool installWebBrowserDependencies = args.Length == 1 &&
    string.Equals(args[0], "--with-web-browsers", StringComparison.Ordinal);
if (args.Length != 0 && !installWebBrowserDependencies)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Install-GraphicalEditorTestPrerequisites.cs " +
        "[--with-web-browsers]")
        .ConfigureAwait(false);
    return 2;
}

try
{
    if (!OperatingSystem.IsLinux())
    {
        await Console.Out.WriteLineAsync(
            "Graphical editor test prerequisites are supplied by this platform.")
            .ConfigureAwait(false);
        return 0;
    }

    if (!File.Exists("/etc/debian_version"))
    {
        throw new PlatformNotSupportedException(
            "Automatic graphical editor test provisioning supports Debian and Ubuntu.");
    }

    await RunPrivilegedAsync("apt-get", ["update"]).ConfigureAwait(false);
    string alsaPackage = await PackageExistsAsync("libasound2t64").ConfigureAwait(false)
        ? "libasound2t64"
        : "libasound2";
    string atkBridgePackage = await SelectPackageAsync(
        "libatk-bridge2.0-0t64",
        "libatk-bridge2.0-0").ConfigureAwait(false);
    string atkPackage = await SelectPackageAsync(
        "libatk1.0-0t64",
        "libatk1.0-0").ConfigureAwait(false);
    string atSpiPackage = await SelectPackageAsync(
        "libatspi2.0-0t64",
        "libatspi2.0-0").ConfigureAwait(false);
    string cupsPackage = await SelectPackageAsync(
        "libcups2t64",
        "libcups2").ConfigureAwait(false);
    string glibPackage = await SelectPackageAsync(
        "libglib2.0-0t64",
        "libglib2.0-0").ConfigureAwait(false);
    string gtkPackage = await SelectPackageAsync(
        "libgtk-3-0t64",
        "libgtk-3-0").ConfigureAwait(false);
    await RunPrivilegedAsync(
        "apt-get",
        [
            "install",
            "--yes",
            "--no-install-recommends",
            alsaPackage,
            atkBridgePackage,
            atkPackage,
            atSpiPackage,
            "libcairo2",
            cupsPackage,
            "libdbus-1-3",
            "libdrm2",
            "libfontconfig1",
            "libgbm1",
            glibPackage,
            gtkPackage,
            "libnspr4",
            "libnss3",
            "libpango-1.0-0",
            "libudev1",
            "libx11-6",
            "libxcb1",
            "libxcomposite1",
            "libxdamage1",
            "libxext6",
            "libxfixes3",
            "libxkbcommon0",
            "libxrandr2",
            "libxshmfence1",
            "libxss1",
            "libxtst6",
            "libvulkan1",
            "mesa-vulkan-drivers",
            "xauth",
            "xclip",
            "xvfb"
        ]).ConfigureAwait(false);
    await RunCheckedAsync(
        "cargo",
        [
            "install",
            "tree-sitter-cli",
            "--version",
            TreeSitterCliVersion,
            "--locked",
            "--no-default-features",
            "--force"
        ]).ConfigureAwait(false);
    await RunCheckedAsync("tree-sitter", ["--version"]).ConfigureAwait(false);
    await RunCheckedAsync("xclip", ["-version"]).ConfigureAwait(false);
    await RunPrivilegedAsync(
        "mkdir",
        ["--parents", "--mode", "1777", "/tmp/.X11-unix"]).ConfigureAwait(false);
    if (installWebBrowserDependencies)
    {
        string repositoryRoot = FindRepositoryRoot();
        string playwrightPath = Path.Join(
            repositoryRoot,
            "tests",
            "vscode",
            "node_modules",
            "playwright-core",
            "cli.js");
        if (!File.Exists(playwrightPath))
        {
            throw new FileNotFoundException(
                "The VS Code fixture is not provisioned. Run Provision-VsCode.cs first.",
                playwrightPath);
        }

        await RunCheckedAsync(
            "node",
            [playwrightPath, "install-deps", "chromium", "firefox", "webkit"])
            .ConfigureAwait(false);
    }

    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidOperationException or
    PlatformNotSupportedException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("The csls repository root was not found.");
}

static Task RunPrivilegedAsync(string executablePath, IReadOnlyList<string> arguments) =>
    string.Equals(Environment.UserName, "root", StringComparison.Ordinal)
        ? RunCheckedAsync(executablePath, arguments)
        : RunCheckedAsync("sudo", ["--non-interactive", executablePath, .. arguments]);

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments)
{
    (int exitCode, string output, string error) = await RunAsync(
        executablePath,
        arguments).ConfigureAwait(false);
    await Console.Out.WriteAsync(output).ConfigureAwait(false);
    await Console.Error.WriteAsync(error).ConfigureAwait(false);
    if (exitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {exitCode}.");
    }
}

static async Task<bool> PackageExistsAsync(string packageName)
{
    (int exitCode, _, _) = await RunAsync(
        "apt-cache",
        ["show", "--no-all-versions", packageName]).ConfigureAwait(false);
    return exitCode == 0;
}

static async Task<string> SelectPackageAsync(
    string preferredPackage,
    string fallbackPackage) =>
    await PackageExistsAsync(preferredPackage).ConfigureAwait(false)
        ? preferredPackage
        : fallbackPackage;

static async Task<(int ExitCode, string Output, string Error)> RunAsync(
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
    startInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The process did not start: {executablePath}");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await standardOutputTask.ConfigureAwait(false);
    string error = await standardErrorTask.ConfigureAwait(false);
    return (process.ExitCode, output, error);
}
