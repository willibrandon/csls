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
        "Usage: dotnet run --file scripts/Verify-ToolPackages.cs [--version <version>]")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length is not 0 and not 2 ||
    args.Length == 2 && !string.Equals(args[0], "--version", StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Verify-ToolPackages.cs [--version <version>]")
        .ConfigureAwait(false);
    return 2;
}

string version = args.Length == 2 ? args[1] : DefaultVersion;
if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsWhiteSpace))
{
    await Console.Error.WriteLineAsync(
        "The package version must be a non-empty NuGet version.").ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string runtimeIdentifier = GetRuntimeIdentifier();
    string verificationRoot = Path.Combine(
        repositoryRoot,
        "artifacts",
        "tool-package-verification");
    RecreateVerificationRoot(repositoryRoot, verificationRoot);
    string packageRoot = Path.Combine(verificationRoot, "packages");
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
            Path.Combine(packageRoot, $"{packageId}.{version}.nupkg"),
            packageId);
    }

    foreach ((string packageId, string commandName, _, string[] workerPaths) in products)
    {
        ValidateImplementationPackage(
            Path.Combine(
                packageRoot,
                $"{packageId}.{runtimeIdentifier}.{version}.nupkg"),
            commandName,
            runtimeIdentifier,
            workerPaths,
            native: true);
        ValidateImplementationPackage(
            Path.Combine(packageRoot, $"{packageId}.any.{version}.nupkg"),
            commandName,
            "any",
            workerPaths,
            native: false);
        await VerifyInstalledToolAsync(
            repositoryRoot,
            verificationRoot,
            packageRoot,
            packageId,
            commandName,
            version).ConfigureAwait(false);
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
    string toolPath = Path.Combine(verificationRoot, "tools", packageId);
    string dotnetHome = Path.Combine(verificationRoot, "dotnet-home", packageId);
    string packages = Path.Combine(verificationRoot, "nuget-packages", packageId);
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
        "--add-source",
        packageRoot,
        "--no-cache",
        "--ignore-failed-sources"
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

static async Task VerifyLanguageServerWorkerAsync(
    string commandPath,
    string verificationRoot,
    IReadOnlyDictionary<string, string> environment)
{
    string fixturePath = Path.Combine(verificationRoot, "lsp-fixture");
    Directory.CreateDirectory(fixturePath);
    await File.WriteAllTextAsync(
        Path.Combine(fixturePath, "Fixture.csproj"),
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """).ConfigureAwait(false);
    await File.WriteAllTextAsync(
        Path.Combine(fixturePath, "Program.cs"),
        """Console.WriteLine("csls package verification");""").ConfigureAwait(false);
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
        string? serverName = initialize.RootElement
            .GetProperty("result")
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
    startInfo.ArgumentList.Add(Path.Combine(verificationRoot, "missing-session.socket"));
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
        string? serverName = initialize.RootElement
            .GetProperty("result")
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
            Path.Combine(toolPath, commandName + ".exe"),
            Path.Combine(toolPath, commandName + ".cmd")
        ]
        : [Path.Combine(toolPath, commandName)];
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
    string requiredPrefix = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts")) +
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
