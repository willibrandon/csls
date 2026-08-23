#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.Diagnostics;

const string Usage = "Usage: dotnet run --file scripts/Capture-Docs.cs";

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Captures verified editor screenshots and rebuilds the csls documentation site.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
    return 2;
}

string? generatedScreenshotPath = null;
try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string screenshotDirectory = Path.Join(
        repositoryRoot,
        "docs-site",
        "src",
        "assets",
        "screenshots");
    string screenshotPath = Path.Join(screenshotDirectory, "helix-hover.svg");
    generatedScreenshotPath = Path.Join(
        screenshotDirectory,
        $"helix-hover-{Guid.NewGuid():N}.svg");
    Directory.CreateDirectory(screenshotDirectory);

    string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    await RunCheckedAsync(
        dotnetPath,
        ["run", "--file", Path.Join("scripts", "Provision-Helix.cs")],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        dotnetPath,
        [
            "test",
            "--project",
            Path.Join("tests", "Csls.Tests", "Csls.Tests.csproj"),
            "--filter",
            "FullyQualifiedName=Csls.Tests.HelixLanguageServerTests.HelixDisplaysHoverFromCsls"
        ],
        repositoryRoot,
        "CSLS_DOCS_SCREENSHOT_PATH",
        generatedScreenshotPath).ConfigureAwait(false);

    if (!File.Exists(generatedScreenshotPath) ||
        new FileInfo(generatedScreenshotPath).Length == 0)
    {
        throw new InvalidDataException("The Helix screenshot was not generated.");
    }

    File.Move(generatedScreenshotPath, screenshotPath, overwrite: true);
    generatedScreenshotPath = null;

    await RunCheckedAsync(
        "npx",
        ["--yes", "npm@12.0.2", "ci", "--prefix", "docs-site"],
        repositoryRoot).ConfigureAwait(false);
    await RunCheckedAsync(
        "npx",
        ["--yes", "npm@12.0.2", "run", "build", "--prefix", "docs-site"],
        repositoryRoot).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(Path.GetRelativePath(repositoryRoot, screenshotPath))
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
finally
{
    if (generatedScreenshotPath is not null)
    {
        File.Delete(generatedScreenshotPath);
    }
}

static async Task RunCheckedAsync(
    string executablePath,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    string? environmentVariableName = null,
    string? environmentVariableValue = null)
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

    if (environmentVariableName is not null)
    {
        startInfo.Environment[environmentVariableName] = environmentVariableValue;
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
        throw new InvalidOperationException(
            $"{executablePath} failed with exit code {process.ExitCode}.");
    }
}
