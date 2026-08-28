#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package NuGet.Versioning
#:package SharpCompress
#:include ScriptSupport.cs

using NuGet.Versioning;
using System.ComponentModel;
using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Publishes the verified platform and web VSIX set to both extension registries.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-VsCodeExtension.cs -- " +
        "--version <version> --packages <directory> [--verify-only <true|false>]")
        .ConfigureAwait(false);
    return 0;
}

string? version = null;
string? packagesPath = null;
bool verifyOnly = false;
for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex += 2)
{
    if (argumentIndex + 1 >= args.Length)
    {
        return await WriteUsageErrorAsync().ConfigureAwait(false);
    }

    switch (args[argumentIndex])
    {
        case "--version":
            version = args[argumentIndex + 1];
            break;
        case "--packages":
            packagesPath = Path.GetFullPath(args[argumentIndex + 1]);
            break;
        case "--verify-only" when bool.TryParse(args[argumentIndex + 1], out bool value):
            verifyOnly = value;
            break;
        default:
            return await WriteUsageErrorAsync().ConfigureAwait(false);
    }
}

if (version is null ||
    !NuGetVersion.TryParse(version, out NuGetVersion? parsedVersion) ||
    !string.Equals(version, parsedVersion.ToNormalizedString(), StringComparison.Ordinal) ||
    parsedVersion.IsPrerelease ||
    packagesPath is null)
{
    return await WriteUsageErrorAsync().ConfigureAwait(false);
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string extensionRoot = Path.Join(repositoryRoot, "editors", "vscode");
    string nodeModulesPath = Path.Join(extensionRoot, "node_modules");
    string vscePath = Path.Join(nodeModulesPath, "@vscode", "vsce", "vsce");
    string ovsxPath = Path.Join(nodeModulesPath, "ovsx", "bin", "ovsx");
    RequireFile(vscePath);
    RequireFile(ovsxPath);

    string[] targets =
    [
        "win32-x64",
        "win32-arm64",
        "linux-x64",
        "linux-arm64",
        "alpine-x64",
        "alpine-arm64",
        "darwin-x64",
        "darwin-arm64",
        "web"
    ];
    string[] packages =
    [
        .. targets.Select(target => Path.Join(
            packagesPath,
            $"csls-{version}-{target}.vsix"))
    ];
    foreach (string package in packages)
    {
        RequireFile(package);
    }

    string[] unexpectedPackages =
    [
        .. Directory.EnumerateFiles(packagesPath, "*.vsix", SearchOption.TopDirectoryOnly)
            .Except(packages, StringComparer.Ordinal)
    ];
    if (unexpectedPackages.Length != 0)
    {
        throw new InvalidDataException(
            $"The release contains unexpected VSIX files: {string.Join(", ", unexpectedPackages.Select(Path.GetFileName))}");
    }

    bool useAzureCredential = string.Equals(
        Environment.GetEnvironmentVariable("CSLS_VSCE_AZURE_CREDENTIAL"),
        "true",
        StringComparison.OrdinalIgnoreCase);
    string? marketplaceToken = useAzureCredential
        ? null
        : RequireEnvironmentVariable("VSCE_PAT");
    string openVsxToken = RequireEnvironmentVariable("OVSX_PAT");
    if (verifyOnly)
    {
        await VerifyAsync(
            vscePath,
            ovsxPath,
            extensionRoot,
            marketplaceToken,
            openVsxToken,
            useAzureCredential).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            "Verified publication access to both extension registries.")
            .ConfigureAwait(false);
        return 0;
    }

    await PublishAsync(
        vscePath,
        packages,
        extensionRoot,
        useAzureCredential ? null : "VSCE_PAT",
        marketplaceToken,
        useAzureCredential).ConfigureAwait(false);
    await PublishAsync(
        ovsxPath,
        packages,
        extensionRoot,
        "OVSX_PAT",
        openVsxToken,
        useAzureCredential: false).ConfigureAwait(false);

    await Console.Out.WriteLineAsync(
        $"Published {packages.Length} verified VSIX files to both extension registries.")
        .ConfigureAwait(false);
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

static async Task<int> WriteUsageErrorAsync()
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Publish-VsCodeExtension.cs -- " +
        "--version <stable-version> --packages <directory> " +
        "[--verify-only <true|false>]")
        .ConfigureAwait(false);
    return 2;
}

static void RequireFile(string path)
{
    if (!File.Exists(path) || new FileInfo(path).Length == 0)
    {
        throw new FileNotFoundException("A required VS Code publication input is missing.", path);
    }
}

static string RequireEnvironmentVariable(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{name} is required for editor publication.")
        : value;
}

static async Task PublishAsync(
    string publisherPath,
    IReadOnlyList<string> packages,
    string workingDirectory,
    string? tokenName,
    string? token,
    bool useAzureCredential)
{
    var arguments = new List<string>
    {
        "publish",
        "--packagePath"
    };
    arguments.AddRange(packages);
    if (useAzureCredential)
    {
        arguments.Add("--azure-credential");
    }

    await RunPublisherAsync(
        publisherPath,
        arguments,
        workingDirectory,
        tokenName,
        token).ConfigureAwait(false);
}

static async Task VerifyAsync(
    string vscePath,
    string ovsxPath,
    string workingDirectory,
    string? marketplaceToken,
    string openVsxToken,
    bool useAzureCredential)
{
    var marketplaceArguments = new List<string>
    {
        "verify-pat",
        "willibrandon"
    };
    if (useAzureCredential)
    {
        marketplaceArguments.Add("--azure-credential");
    }

    await RunPublisherAsync(
        vscePath,
        marketplaceArguments,
        workingDirectory,
        useAzureCredential ? null : "VSCE_PAT",
        marketplaceToken).ConfigureAwait(false);
    await RunPublisherAsync(
        ovsxPath,
        ["verify-pat", "willibrandon"],
        workingDirectory,
        "OVSX_PAT",
        openVsxToken).ConfigureAwait(false);
}

static async Task RunPublisherAsync(
    string publisherPath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    string? tokenName,
    string? token)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "node",
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory
    };
    startInfo.ArgumentList.Add(publisherPath);
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    if (tokenName is not null && token is not null)
    {
        startInfo.Environment[tokenName] = token;
    }
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"The editor publisher did not start: {publisherPath}");
    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string output = await outputTask.ConfigureAwait(false);
    string error = await errorTask.ConfigureAwait(false);
    await Console.Out.WriteAsync(output).ConfigureAwait(false);
    await Console.Error.WriteAsync(error).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Editor publication failed with exit code {process.ExitCode}: {Path.GetFileName(publisherPath)}");
    }
}
