#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property PackAsTool=false
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

const string DefaultVersion = "0.1.0-verification";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Packs, installs, updates, runs, and uninstalls the csls .NET tools.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-ToolPackages.cs " +
        "[--version <version>] [--runtime <rid>] [--validation <execute|package>] " +
        "[--target-dotnet-root <path>]")
        .ConfigureAwait(false);
    return 0;
}

string version = DefaultVersion;
string? requestedRuntimeIdentifier = null;
bool executePackages = true;
string? targetDotnetRoot = null;
for (int argumentIndex = 0; argumentIndex < args.Length; argumentIndex += 2)
{
    if (argumentIndex + 1 >= args.Length)
    {
        await WriteUsageErrorAsync().ConfigureAwait(false);
        return 2;
    }

    string value = args[argumentIndex + 1];
    switch (args[argumentIndex])
    {
        case "--version":
            version = value;
            break;
        case "--runtime":
            requestedRuntimeIdentifier = value;
            break;
        case "--validation" when string.Equals(value, "execute", StringComparison.Ordinal):
            executePackages = true;
            break;
        case "--validation" when string.Equals(value, "package", StringComparison.Ordinal):
            executePackages = false;
            break;
        case "--target-dotnet-root":
            targetDotnetRoot = Path.GetFullPath(value);
            break;
        default:
            await WriteUsageErrorAsync().ConfigureAwait(false);
            return 2;
    }
}

if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsWhiteSpace))
{
    await Console.Error.WriteLineAsync(
        "The package version must be a non-empty NuGet version.").ConfigureAwait(false);
    return 2;
}

string[] supportedRuntimeIdentifiers =
[
    "win-x64",
    "win-arm64",
    "win-x86",
    "linux-x64",
    "linux-arm64",
    "linux-musl-x64",
    "linux-musl-arm64",
    "osx-x64",
    "osx-arm64"
];
if (requestedRuntimeIdentifier is not null &&
    !supportedRuntimeIdentifiers.Contains(requestedRuntimeIdentifier, StringComparer.Ordinal))
{
    await Console.Error.WriteLineAsync(
        $"Unsupported package runtime identifier: {requestedRuntimeIdentifier}")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string runtimeIdentifier = requestedRuntimeIdentifier ?? GetRuntimeIdentifier();
    string hostRuntimeIdentifier = GetRuntimeIdentifier();
    string verificationRoot = Path.Join(
        repositoryRoot,
        "artifacts",
        "tool-package-verification");
    RecreateVerificationRoot(repositoryRoot, verificationRoot);
    string packageRoot = Path.Join(verificationRoot, "packages");
    Directory.CreateDirectory(packageRoot);

    (string PackageId, string CommandName, string Project, string[] WorkerPaths)[] products =
    [
        (
            "csls",
            "csls",
            "src/Csls.App/Csls.App.csproj",
            ["workers/server/csls-worker", "workers/cli/csls-cli-worker"]),
        (
            "csls-mcp",
            "csls-mcp",
            "src/Csls.Mcp/Csls.Mcp.csproj",
            ["workers/mcp/csls-mcp-worker"])
    ];

    foreach ((string packageId, _, string project, _) in products)
    {
        await PackAsync(repositoryRoot, packageRoot, project, version, runtimeIdentifier: null)
            .ConfigureAwait(false);
        await PackAsync(repositoryRoot, packageRoot, project, version, runtimeIdentifier)
            .ConfigureAwait(false);
        await PackAsync(repositoryRoot, packageRoot, project, version, "any")
            .ConfigureAwait(false);
        ValidateManifestPackage(
            Path.Join(packageRoot, $"{packageId}.{version}.nupkg"),
            packageId);
    }

    foreach ((string packageId, string commandName, _, string[] workerPaths) in products)
    {
        ValidateImplementationPackage(
            Path.Join(
                packageRoot,
                $"{packageId}.{runtimeIdentifier}.{version}.nupkg"),
            commandName,
            runtimeIdentifier,
            workerPaths,
            native: true);
        ValidateImplementationPackage(
            Path.Join(packageRoot, $"{packageId}.any.{version}.nupkg"),
            commandName,
            "any",
            workerPaths,
            native: false);
        if (executePackages)
        {
            if (string.Equals(
                runtimeIdentifier,
                hostRuntimeIdentifier,
                StringComparison.Ordinal))
            {
                await VerifyInstalledToolAsync(
                    repositoryRoot,
                    verificationRoot,
                    packageRoot,
                    packageId,
                    commandName,
                    version).ConfigureAwait(false);
            }
            else
            {
                await VerifyImplementationToolAsync(
                    repositoryRoot,
                    verificationRoot,
                    packageRoot,
                    packageId,
                    commandName,
                    version,
                    runtimeIdentifier,
                    targetDotnetRoot).ConfigureAwait(false);
            }
        }
    }

    await Console.Out.WriteLineAsync(
        $"Verified csls and csls-mcp tool packages for {runtimeIdentifier} and any.")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidDataException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task WriteUsageErrorAsync() => await Console.Error.WriteLineAsync(
    "Usage: dotnet run --file scripts/Verify-ToolPackages.cs " +
    "[--version <version>] [--runtime <rid>] [--validation <execute|package>] " +
    "[--target-dotnet-root <path>]")
    .ConfigureAwait(false);

static async Task PackAsync(
    string repositoryRoot,
    string packageRoot,
    string project,
    string version,
    string? runtimeIdentifier)
{
    List<string> arguments =
    [
        "pack",
        project,
        "--configuration",
        "Release",
        "--output",
        packageRoot,
        $"-p:Version={version}",
        $"-p:PackageVersion={version}"
    ];
    if (runtimeIdentifier is not null)
    {
        arguments.Add("--runtime");
        arguments.Add(runtimeIdentifier);
        if (!string.Equals(runtimeIdentifier, "any", StringComparison.Ordinal) &&
            !string.Equals(runtimeIdentifier, "win-x86", StringComparison.Ordinal))
        {
            arguments.Add("-p:IlcGenerateMstatFile=true");
        }
    }

    await RunCheckedAsync(
        ResolveDotNetHost(),
        arguments,
        repositoryRoot,
        environment: null).ConfigureAwait(false);
}

static void ValidateManifestPackage(string packagePath, string packageId)
{
    using ZipArchive archive = OpenRequiredPackage(packagePath);
    RequireEntry(archive, "README.md");
    RequireEntry(archive, "LICENSE");
    ZipArchiveEntry settingsEntry = RequireEntry(
        archive,
        "tools/any/any/DotnetToolSettings.xml");
    XDocument settings = LoadXml(settingsEntry);
    string[] packageIds =
    [
        .. settings
            .Descendants("RuntimeIdentifierPackage")
            .Select(static element =>
                (string?)element.Attribute("Id") ?? string.Empty)
    ];
    string[] expectedPackageIds =
    [
        $"{packageId}.win-x64",
        $"{packageId}.win-arm64",
        $"{packageId}.win-x86",
        $"{packageId}.linux-x64",
        $"{packageId}.linux-arm64",
        $"{packageId}.linux-musl-x64",
        $"{packageId}.linux-musl-arm64",
        $"{packageId}.osx-x64",
        $"{packageId}.osx-arm64",
        $"{packageId}.any"
    ];
    if (!packageIds.SequenceEqual(expectedPackageIds, StringComparer.Ordinal))
    {
        throw new InvalidDataException(
            $"{packagePath} does not declare the exact supported RID package set.");
    }

    ValidatePackageType(archive, "DotnetTool");
    ValidateNoForbiddenEntries(archive);
}

static void ValidateImplementationPackage(
    string packagePath,
    string commandName,
    string runtimeIdentifier,
    IReadOnlyList<string> workerPaths,
    bool native)
{
    using ZipArchive archive = OpenRequiredPackage(packagePath);
    string root = native
        ? $"tools/any/{runtimeIdentifier}"
        : "tools/net10.0/any";
    string executableExtension = native && runtimeIdentifier.StartsWith(
        "win-",
        StringComparison.Ordinal)
        ? ".exe"
        : string.Empty;
    string launcherName = native
        ? commandName + executableExtension
        : commandName + ".dll";
    RequireEntry(archive, $"{root}/{launcherName}");
    foreach (string workerPath in workerPaths)
    {
        string workerName = native
            ? workerPath + executableExtension
            : workerPath + ".dll";
        RequireEntry(archive, $"{root}/{workerName}");
    }

    XDocument settings = LoadXml(RequireEntry(
        archive,
        $"{root}/DotnetToolSettings.xml"));
    string expectedRunner = native ? "executable" : "dotnet";
    string? runner = (string?)settings.Descendants("Command").Single().Attribute("Runner");
    if (!string.Equals(runner, expectedRunner, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"{packagePath} uses runner '{runner}' instead of '{expectedRunner}'.");
    }

    ValidatePackageType(archive, "DotnetToolRidPackage");
    ValidateNoForbiddenEntries(archive);
}

static async Task VerifyInstalledToolAsync(
    string repositoryRoot,
    string verificationRoot,
    string packageRoot,
    string packageId,
    string commandName,
    string version)
{
    string toolPath = Path.Join(verificationRoot, "tools", packageId);
    string dotnetHome = Path.Join(verificationRoot, "dotnet-home", packageId);
    string packages = Path.Join(verificationRoot, "nuget-packages", packageId);
    string nugetConfiguration = Path.Join(verificationRoot, "NuGet.Config");
    WriteVerificationNuGetConfiguration(nugetConfiguration, packageRoot);
    var environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_HOME"] = dotnetHome,
        ["NUGET_PACKAGES"] = packages,
        ["DOTNET_NOLOGO"] = "1"
    };
    string[] sourceArguments =
    [
        "--version",
        version,
        "--tool-path",
        toolPath,
        "--configfile",
        nugetConfiguration,
        "--no-cache",
    ];
    await RunCheckedAsync(
        ResolveDotNetHost(),
        ["tool", "install", packageId, .. sourceArguments],
        repositoryRoot,
        environment).ConfigureAwait(false);

    string commandPath = ResolveInstalledCommand(toolPath, commandName);
    string versionOutput = await RunCheckedAsync(
        commandPath,
        ["--version"],
        repositoryRoot,
        environment).ConfigureAwait(false);
    if (!versionOutput.Contains(version, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Installed {packageId} reported an unexpected version: {versionOutput.Trim()}");
    }

    await RunCheckedAsync(
        commandPath,
        ["--help"],
        repositoryRoot,
        environment).ConfigureAwait(false);
    if (string.Equals(commandName, "csls", StringComparison.Ordinal))
    {
        await VerifyAgentCommandsAsync(
            commandPath,
            repositoryRoot,
            environment).ConfigureAwait(false);
        await VerifyLanguageServerWorkerAsync(
            commandPath,
            verificationRoot,
            environment).ConfigureAwait(false);
    }
    else
    {
        await VerifyMcpWorkerAsync(
            commandPath,
            verificationRoot,
            repositoryRoot,
            environment).ConfigureAwait(false);
    }

    await RunCheckedAsync(
        ResolveDotNetHost(),
        ["tool", "update", packageId, .. sourceArguments],
        repositoryRoot,
        environment).ConfigureAwait(false);
    await RunCheckedAsync(
        ResolveDotNetHost(),
        ["tool", "uninstall", packageId, "--tool-path", toolPath],
        repositoryRoot,
        environment).ConfigureAwait(false);
    if (File.Exists(commandPath))
    {
        throw new InvalidDataException(
            $"Uninstalling {packageId} left its command shim at {commandPath}.");
    }
}

static void WriteVerificationNuGetConfiguration(
    string configurationPath,
    string packageRoot)
{
    var configuration = new XDocument(
        new XElement(
            "configuration",
            new XElement(
                "packageSources",
                new XElement("clear"),
                new XElement(
                    "add",
                    new XAttribute("key", "verification"),
                    new XAttribute("value", packageRoot)),
                new XElement(
                    "add",
                    new XAttribute("key", "nuget.org"),
                    new XAttribute("value", "https://api.nuget.org/v3/index.json"),
                    new XAttribute("protocolVersion", "3"))),
            new XElement(
                "packageSourceMapping",
                new XElement(
                    "packageSource",
                    new XAttribute("key", "verification"),
                    new XElement("package", new XAttribute("pattern", "csls*"))),
                new XElement(
                    "packageSource",
                    new XAttribute("key", "nuget.org"),
                    new XElement(
                        "package",
                        new XAttribute("pattern", "Microsoft.NETCore.App.Host.*"))))));
    configuration.Save(configurationPath);
}

static async Task VerifyImplementationToolAsync(
    string repositoryRoot,
    string verificationRoot,
    string packageRoot,
    string packageId,
    string commandName,
    string version,
    string runtimeIdentifier,
    string? targetDotnetRoot)
{
    string extractionPath = Path.Join(
        verificationRoot,
        "implementations",
        packageId,
        runtimeIdentifier);
    Directory.CreateDirectory(extractionPath);
    await ZipFile.ExtractToDirectoryAsync(
        Path.Join(
            packageRoot,
            $"{packageId}.{runtimeIdentifier}.{version}.nupkg"),
        extractionPath,
        overwriteFiles: true,
        CancellationToken.None).ConfigureAwait(false);
    string executableExtension = runtimeIdentifier.StartsWith(
        "win-",
        StringComparison.Ordinal)
        ? ".exe"
        : string.Empty;
    string commandPath = Path.Join(
        extractionPath,
        "tools",
        "any",
        runtimeIdentifier,
        commandName + executableExtension);
    if (!File.Exists(commandPath))
    {
        throw new InvalidDataException(
            $"The extracted {packageId} implementation omitted {commandPath}.");
    }

    ScriptSupport.EnsureExecutable(commandPath);
    var environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_HOME"] = Path.Join(
            verificationRoot,
            "dotnet-home",
            packageId,
            runtimeIdentifier),
        ["DOTNET_NOLOGO"] = "1"
    };
    if (targetDotnetRoot is not null)
    {
        if (!File.Exists(Path.Join(
            targetDotnetRoot,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
        {
            throw new FileNotFoundException(
                "The target .NET root does not contain a dotnet host.",
                targetDotnetRoot);
        }

        string rootVariable = string.Equals(
            runtimeIdentifier,
            "win-x86",
            StringComparison.Ordinal)
            ? "DOTNET_ROOT(x86)"
            : "DOTNET_ROOT";
        environment[rootVariable] = targetDotnetRoot;
    }
    string versionOutput = await RunCheckedAsync(
        commandPath,
        ["--version"],
        repositoryRoot,
        environment).ConfigureAwait(false);
    if (!versionOutput.Contains(version, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The {runtimeIdentifier} {packageId} implementation reported an unexpected " +
            $"version: {versionOutput.Trim()}");
    }

    await RunCheckedAsync(
        commandPath,
        ["--help"],
        repositoryRoot,
        environment).ConfigureAwait(false);
    if (string.Equals(commandName, "csls", StringComparison.Ordinal))
    {
        await VerifyAgentCommandsAsync(
            commandPath,
            repositoryRoot,
            environment).ConfigureAwait(false);
        await VerifyLanguageServerWorkerAsync(
            commandPath,
            verificationRoot,
            environment).ConfigureAwait(false);
    }
    else
    {
        await VerifyMcpWorkerAsync(
            commandPath,
            verificationRoot,
            repositoryRoot,
            environment).ConfigureAwait(false);
    }
}

static async Task VerifyAgentCommandsAsync(
    string commandPath,
    string workingDirectory,
    IReadOnlyDictionary<string, string> environment)
{
    string mcpHelp = await RunCheckedAsync(
        commandPath,
        ["agent", "mcp", "--help"],
        workingDirectory,
        environment).ConfigureAwait(false);
    string[] requiredOptions = ["--session", "--socket", "--workspace"];
    if (requiredOptions.Any(option => !mcpHelp.Contains(option, StringComparison.Ordinal)))
    {
        throw new InvalidDataException(
            "The installed csls agent MCP command omitted a required connection option.");
    }

    string skill = await RunCheckedAsync(
        commandPath,
        ["agent", "init", "--stdout"],
        workingDirectory,
        environment).ConfigureAwait(false);
    if (!skill.Contains("name: csls", StringComparison.Ordinal) ||
        !skill.Contains("csls agent mcp --workspace .", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "The installed csls agent init command returned incomplete skill content.");
    }
}

static async Task VerifyLanguageServerWorkerAsync(
    string commandPath,
    string verificationRoot,
    IReadOnlyDictionary<string, string> environment)
{
    string fixturePath = Path.Join(verificationRoot, "lsp-fixture");
    Directory.CreateDirectory(fixturePath);
    await File.WriteAllTextAsync(
        Path.Join(fixturePath, "Fixture.csproj"),
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """).ConfigureAwait(false);
    await File.WriteAllTextAsync(
        Path.Join(fixturePath, "Program.cs"),
        """Console.WriteLine("csls package verification");""").ConfigureAwait(false);
    await VerifyDoctorAsync(
        commandPath,
        fixturePath,
        environment).ConfigureAwait(false);
    string rootUri = new Uri(fixturePath + Path.DirectorySeparatorChar).AbsoluteUri;
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    var startInfo = new ProcessStartInfo
    {
        FileName = commandPath,
        WorkingDirectory = fixturePath,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    ApplyEnvironment(startInfo, environment);
    using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        "The installed csls language server did not start.");
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
    string initializeRequest =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{" +
        $"\"processId\":{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}," +
        $"\"rootUri\":\"{JsonEncodedText.Encode(rootUri)}\",\"capabilities\":{{}}}}}}";
    try
    {
        await WriteLspMessageAsync(
            process.StandardInput.BaseStream,
            initializeRequest,
            timeout.Token).ConfigureAwait(false);
        using var initialize = JsonDocument.Parse(await ReadLspMessageAsync(
            process.StandardOutput.BaseStream,
            timeout.Token).ConfigureAwait(false));
        if (!initialize.RootElement.TryGetProperty("result", out JsonElement result))
        {
            throw new InvalidDataException(
                $"The installed language server rejected initialization: " +
                initialize.RootElement.GetRawText());
        }

        string? serverName = result
            .GetProperty("serverInfo")
            .GetProperty("name")
            .GetString();
        if (!string.Equals(serverName, "csls", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The installed language server initialized as '{serverName}'.");
        }

        await WriteLspMessageAsync(
            process.StandardInput.BaseStream,
            """{"jsonrpc":"2.0","method":"initialized","params":{}}""",
            timeout.Token).ConfigureAwait(false);
        await WriteLspMessageAsync(
            process.StandardInput.BaseStream,
            """{"jsonrpc":"2.0","id":2,"method":"shutdown","params":null}""",
            timeout.Token).ConfigureAwait(false);
        using var shutdown = JsonDocument.Parse(await ReadLspMessageAsync(
            process.StandardOutput.BaseStream,
            timeout.Token).ConfigureAwait(false));
        if (shutdown.RootElement.GetProperty("id").GetInt32() != 2)
        {
            throw new InvalidDataException(
                "The installed language server returned the wrong shutdown response.");
        }

        await WriteLspMessageAsync(
            process.StandardInput.BaseStream,
            """{"jsonrpc":"2.0","method":"exit","params":null}""",
            timeout.Token).ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The installed language server exited with {process.ExitCode}: {standardError}");
        }
    }
    catch (Exception exception)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        string standardError = await standardErrorTask.ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Installed language-server protocol verification failed: {exception.Message}" +
            $"{Environment.NewLine}{standardError}",
            exception);
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

static async Task VerifyDoctorAsync(
    string commandPath,
    string fixturePath,
    IReadOnlyDictionary<string, string> environment)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    var startInfo = new ProcessStartInfo
    {
        FileName = commandPath,
        WorkingDirectory = fixturePath,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("doctor");
    startInfo.ArgumentList.Add(fixturePath);
    startInfo.ArgumentList.Add("--json");
    ApplyEnvironment(startInfo, environment);
    using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        "The installed csls doctor did not start.");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"The installed csls doctor exited with {process.ExitCode}: {standardError}");
    }

    using var document = JsonDocument.Parse(standardOutput);
    JsonElement root = document.RootElement;
    if (!root.GetProperty("success").GetBoolean() ||
        root.GetProperty("data").GetProperty("projects").GetArrayLength() == 0)
    {
        throw new InvalidDataException(
            $"The installed csls doctor did not load its project: {standardOutput}");
    }
}

static async Task VerifyMcpWorkerAsync(
    string commandPath,
    string verificationRoot,
    string workingDirectory,
    IReadOnlyDictionary<string, string> environment)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
    var startInfo = new ProcessStartInfo
    {
        FileName = commandPath,
        WorkingDirectory = workingDirectory,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("--socket");
    startInfo.ArgumentList.Add(Path.Join(verificationRoot, "missing-session.socket"));
    ApplyEnvironment(startInfo, environment);
    using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        "The installed csls MCP server did not start.");
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(timeout.Token);
    try
    {
        await process.StandardInput.WriteLineAsync(
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"csls-package-verifier","version":"1.0"}}}
            """).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        string initializeText = await process.StandardOutput
            .ReadLineAsync(timeout.Token)
            .ConfigureAwait(false) ?? throw new EndOfStreamException(
                "The installed MCP server returned no initialize response.");
        using var initialize = JsonDocument.Parse(initializeText);
        if (!initialize.RootElement.TryGetProperty("result", out JsonElement result))
        {
            throw new InvalidDataException(
                $"The installed MCP server rejected initialization: " +
                initialize.RootElement.GetRawText());
        }

        string? serverName = result
            .GetProperty("serverInfo")
            .GetProperty("name")
            .GetString();
        if (!string.Equals(serverName, "csls", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The installed MCP server initialized as '{serverName}'.");
        }

        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""")
            .ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_session","arguments":{}}}""")
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        string toolText = await process.StandardOutput
            .ReadLineAsync(timeout.Token)
            .ConfigureAwait(false) ?? throw new EndOfStreamException(
                "The installed MCP server returned no tool response.");
        using var toolResponse = JsonDocument.Parse(toolText);
        if (toolResponse.RootElement.GetProperty("id").GetInt32() != 2 ||
            !toolResponse.RootElement.TryGetProperty("result", out _))
        {
            throw new InvalidDataException(
                "The installed MCP worker did not process the session tool call.");
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The installed MCP server exited with {process.ExitCode}: {standardError}");
        }
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

static async Task WriteLspMessageAsync(
    Stream stream,
    string json,
    CancellationToken cancellationToken)
{
    byte[] payload = Encoding.UTF8.GetBytes(json);
    byte[] header = Encoding.ASCII.GetBytes(
        $"Content-Length: {payload.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
    await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task<string> ReadLspMessageAsync(
    Stream stream,
    CancellationToken cancellationToken)
{
    var header = new List<byte>(capacity: 128);
    while (header.Count < 8_192)
    {
        byte[] oneByte = new byte[1];
        int read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException("The installed language server closed standard output.");
        }

        header.Add(oneByte[0]);
        int count = header.Count;
        if (count >= 4 &&
            header[count - 4] == '\r' &&
            header[count - 3] == '\n' &&
            header[count - 2] == '\r' &&
            header[count - 1] == '\n')
        {
            string headerText = Encoding.ASCII.GetString([.. header]);
            string contentLengthHeader = headerText
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(static line => line.StartsWith(
                    "Content-Length:",
                    StringComparison.OrdinalIgnoreCase));
            int contentLength = int.Parse(
                contentLengthHeader["Content-Length:".Length..].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            byte[] payload = new byte[contentLength];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(payload);
        }
    }

    throw new InvalidDataException("The installed language server returned oversized headers.");
}

static ZipArchive OpenRequiredPackage(string packagePath)
{
    if (!File.Exists(packagePath))
    {
        throw new FileNotFoundException("The expected tool package was not produced.", packagePath);
    }

    return ZipFile.OpenRead(packagePath);
}

static ZipArchiveEntry RequireEntry(ZipArchive archive, string entryName) =>
    archive.GetEntry(entryName) ?? throw new InvalidDataException(
        $"{archive.Comment} is missing package entry '{entryName}'.");

static XDocument LoadXml(ZipArchiveEntry entry)
{
    using Stream stream = entry.Open();
    return XDocument.Load(stream, LoadOptions.None);
}

static void ValidatePackageType(ZipArchive archive, string expectedType)
{
    ZipArchiveEntry nuspec = archive.Entries.Single(static entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    XDocument document = LoadXml(nuspec);
    string? actualType = document
        .Descendants()
        .Single(static element => element.Name.LocalName == "packageType")
        .Attribute("name")?
        .Value;
    if (!string.Equals(actualType, expectedType, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"{nuspec.FullName} uses package type '{actualType}' instead of '{expectedType}'.");
    }
}

static void ValidateNoForbiddenEntries(ZipArchive archive)
{
    string[] forbiddenEntries =
    [
        .. archive.Entries
            .Select(static entry => entry.FullName.Replace('\\', '/'))
            .Where(static entry =>
                entry.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith(".dwarf", StringComparison.OrdinalIgnoreCase) ||
                entry.Contains(".dSYM/", StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                !entry.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) &&
                !entry.EndsWith("/DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase))
    ];
    if (forbiddenEntries.Length != 0)
    {
        throw new InvalidDataException(
            $"A tool package contains forbidden artifacts: {string.Join(", ", forbiddenEntries)}");
    }
}

static string ResolveInstalledCommand(string toolPath, string commandName)
{
    string[] candidates = OperatingSystem.IsWindows()
        ?
        [
            Path.Join(toolPath, commandName + ".exe"),
            Path.Join(toolPath, commandName + ".cmd")
        ]
        : [Path.Join(toolPath, commandName)];
    return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
        $"The installed {commandName} command shim was not found in {toolPath}.");
}

static async Task<string> RunCheckedAsync(
    string executable,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        WorkingDirectory = workingDirectory,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        UseShellExecute = false
    };
    foreach (string argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    ApplyEnvironment(startInfo, environment);

    using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException(
        $"The process did not start: {executable}");
    Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
    Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    string standardOutput = await standardOutputTask.ConfigureAwait(false);
    string standardError = await standardErrorTask.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"{executable} {string.Join(' ', arguments)} failed with exit code " +
            $"{process.ExitCode}:{Environment.NewLine}{standardOutput}{standardError}");
    }

    await Console.Out.WriteAsync(standardOutput).ConfigureAwait(false);
    await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
    return standardOutput + standardError;
}

static void ApplyEnvironment(
    ProcessStartInfo startInfo,
    IReadOnlyDictionary<string, string>? environment)
{
    if (environment is null)
    {
        return;
    }

    foreach ((string name, string value) in environment)
    {
        startInfo.Environment[name] = value;
    }
}

static string ResolveDotNetHost() =>
    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

static string GetRuntimeIdentifier()
{
    string architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new InvalidOperationException(
            $"Native AOT verification does not support {RuntimeInformation.OSArchitecture}.")
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

    throw new InvalidOperationException(
        $"Native AOT verification does not support {RuntimeInformation.OSDescription}.");
}

static void RecreateVerificationRoot(string repositoryRoot, string verificationRoot)
{
    string requiredPrefix = Path.GetFullPath(Path.Join(repositoryRoot, "artifacts")) +
        Path.DirectorySeparatorChar;
    string fullPath = Path.GetFullPath(verificationRoot);
    if (!fullPath.StartsWith(requiredPrefix, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            $"Refusing to recreate a verification directory outside artifacts: {fullPath}");
    }

    if (Directory.Exists(fullPath))
    {
        Directory.Delete(fullPath, recursive: true);
    }

    Directory.CreateDirectory(fullPath);
}
