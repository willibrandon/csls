#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Installs system, editor-oracle, and restored .NET dependencies for the csls dev container.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Initialize-DevContainer.cs").ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Initialize-DevContainer.cs").ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    if (OperatingSystem.IsLinux() && string.Equals(
        Environment.GetEnvironmentVariable("CSLS_DEV_CONTAINER"),
        "true",
        StringComparison.OrdinalIgnoreCase))
    {
        string artifactsRoot = Environment.GetEnvironmentVariable("CSLS_ARTIFACTS_ROOT")
            ?? throw new InvalidOperationException(
                "CSLS_ARTIFACTS_ROOT is required in the development container.");
        string cacheRoot = Environment.GetEnvironmentVariable("CSLS_CACHE_ROOT")
            ?? throw new InvalidOperationException(
                "CSLS_CACHE_ROOT is required in the development container.");
        string normalizedCacheRoot = Path.GetFullPath(cacheRoot);
        string normalizedArtifactsRoot = Path.GetFullPath(artifactsRoot);
        if (!normalizedArtifactsRoot.StartsWith(
                normalizedCacheRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CSLS_ARTIFACTS_ROOT must be inside CSLS_CACHE_ROOT.");
        }

        await RunCheckedAsync(
            "sudo",
            [
                "chown",
                $"{Environment.UserName}:{Environment.UserName}",
                normalizedCacheRoot
            ],
            repositoryRoot).ConfigureAwait(false);
    }

    if (OperatingSystem.IsLinux() && File.Exists("/etc/debian_version"))
    {
        string packageManager = string.Equals(
            Environment.UserName,
            "root",
            StringComparison.Ordinal)
            ? "apt-get"
            : "sudo";
        IReadOnlyList<string> updateArguments = string.Equals(
            packageManager,
            "sudo",
            StringComparison.Ordinal)
            ? ["apt-get", "update"]
            : ["update"];
        IReadOnlyList<string> installArguments = string.Equals(
            packageManager,
            "sudo",
            StringComparison.Ordinal)
            ?
            [
                "apt-get",
                "install",
                "--yes",
                "--no-install-recommends",
                "build-essential",
                "clang",
                "git",
                "libncurses-dev",
                "zlib1g-dev"
            ]
            :
            [
                "install",
                "--yes",
                "--no-install-recommends",
                "build-essential",
                "clang",
                "git",
                "libncurses-dev",
                "zlib1g-dev"
            ];
        await RunCheckedAsync(packageManager, updateArguments, repositoryRoot)
            .ConfigureAwait(false);
        await RunCheckedAsync(packageManager, installArguments, repositoryRoot)
            .ConfigureAwait(false);
    }

    var dependencyTasks = new List<Task>
    {
        RunCheckedAsync(dotnetPath, ["restore", "Csls.slnx"], repositoryRoot),
        RunCheckedAsync(
            "cargo",
            [
                "fetch",
                "--locked",
                "--manifest-path",
                Path.Join("editors", "zed", "Cargo.toml")
            ],
            repositoryRoot)
    };
    string scriptsDirectory = Path.Join(repositoryRoot, "scripts");
    foreach (string fileAppPath in Directory
        .EnumerateFiles(scriptsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal))
    {
        if (string.Equals(
                Path.GetFileName(fileAppPath),
                "Initialize-DevContainer.cs",
                StringComparison.Ordinal) ||
            !await IsFileAppAsync(fileAppPath).ConfigureAwait(false))
        {
            continue;
        }

        dependencyTasks.Add(RunCheckedAsync(
            dotnetPath,
            ["restore", Path.GetRelativePath(repositoryRoot, fileAppPath)],
            repositoryRoot));
    }

    await Task.WhenAll(dependencyTasks).ConfigureAwait(false);

    string[] provisioners =
    [
        "Provision-Actionlint.cs",
        "Provision-CsharpLsOracle.cs",
        "Provision-RoslynLanguageServerOracle.cs",
        "Provision-LegacyBuildHost.cs",
        "Provision-Fresh.cs",
        "Provision-Emacs.cs",
        "Provision-Helix.cs",
        "Provision-Neovim.cs",
        "Provision-WasiSdk.cs",
        "Provision-Zed.cs"
    ];
    Task[] provisionTasks =
    [
        .. provisioners.Select(provisioner => RunCheckedAsync(
            dotnetPath,
            ["run", "--file", Path.Join("scripts", provisioner)],
            repositoryRoot)),
        RunCheckedAsync(
            dotnetPath,
            [
                "run",
                "--file",
                Path.Join("scripts", "Provision-VsCode.cs"),
                "--",
                "--with-web-browsers"
            ],
            repositoryRoot),
        RunCheckedAsync(
            dotnetPath,
            ["run", "--file", Path.Join("scripts", "Provision-VsCodeRemoteServer.cs")],
            repositoryRoot)
    ];
    await Task.WhenAll(provisionTasks).ConfigureAwait(false);

    Task[] validationTasks =
    [
        RunCheckedAsync(
            dotnetPath,
            [
                "run",
                "--file",
                Path.Join("scripts", "Install-GraphicalEditorTestPrerequisites.cs"),
                "--",
                "--with-web-browsers"
            ],
            repositoryRoot),
        RunCheckedAsync(
            dotnetPath,
            ["run", "--file", Path.Join("scripts", "Verify-Repository.cs")],
            repositoryRoot),
        RunCheckedAsync(
            dotnetPath,
            ["run", "--file", Path.Join("scripts", "Verify-GitHubActions.cs")],
            repositoryRoot),
        RunCheckedAsync(
            dotnetPath,
            ["run", "--file", Path.Join("scripts", "Build-ZedExtension.cs")],
            repositoryRoot),
        RunCheckedAsync(
            "npm",
            ["ci", "--prefix", "docs-site"],
            repositoryRoot)
    ];
    await Task.WhenAll(validationTasks).ConfigureAwait(false);
    await Console.Out.WriteLineAsync("The csls development container is ready.")
        .ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is
    IOException or
    InvalidOperationException or
    UnauthorizedAccessException)
{
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task<bool> IsFileAppAsync(string path)
{
    int remainingLines = 8;
    await foreach (string line in File.ReadLinesAsync(path).ConfigureAwait(false))
    {
        if (line.StartsWith("#:", StringComparison.Ordinal))
        {
            return true;
        }

        remainingLines--;
        if (remainingLines == 0)
        {
            return false;
        }
    }

    return false;
}

static async Task RunCheckedAsync(
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
    await Console.Out.WriteAsync(standardOutput).ConfigureAwait(false);
    await Console.Error.WriteAsync(standardError).ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        string command = string.Join(' ', arguments.Prepend(executablePath));
        throw new InvalidOperationException(
            $"{command} failed with exit code {process.ExitCode}.");
    }
}
