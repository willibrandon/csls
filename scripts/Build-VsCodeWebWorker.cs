#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property LangVersion=14.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:package SharpCompress
#:include ScriptSupport.cs

using System.ComponentModel;
using System.Diagnostics;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    await Console.Out.WriteLineAsync(
        "Builds the csls browser WebAssembly worker for the VS Code extension.")
        .ConfigureAwait(false);
    await Console.Out.WriteLineAsync(
        "Usage: dotnet run --file scripts/Build-VsCodeWebWorker.cs")
        .ConfigureAwait(false);
    return 0;
}

if (args.Length != 0)
{
    await Console.Error.WriteLineAsync(
        "Usage: dotnet run --file scripts/Build-VsCodeWebWorker.cs")
        .ConfigureAwait(false);
    return 2;
}

try
{
    string repositoryRoot = ScriptSupport.FindRepositoryRoot();
    string artifactsRoot = Path.Join(repositoryRoot, "artifacts");
    string publishPath = Path.Join(artifactsRoot, "vscode-web-worker");
    string outputPath = Path.Join(repositoryRoot, "editors", "vscode", "dist", "browserServer");
    RecreateDirectory(publishPath);
    RecreateDirectory(outputPath);
    await RunCheckedAsync(
        "dotnet",
        [
            "publish",
            "src/Csls.Web.Worker/Csls.Web.Worker.csproj",
            "--configuration",
            "Release",
            "--output",
            publishPath
        ],
        repositoryRoot).ConfigureAwait(false);

    CopyRequiredFile(
        Path.Join(publishPath, "wwwroot", "cslsBrowserWorker.js"),
        Path.Join(outputPath, "cslsBrowserWorker.js"));
    CopyFramework(
        Path.Join(publishPath, "wwwroot", "_framework"),
        Path.Join(outputPath, "_framework"));
    await Console.Out.WriteLineAsync(outputPath).ConfigureAwait(false);
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

static void RecreateDirectory(string path)
{
    if (Directory.Exists(path))
    {
        Directory.Delete(path, recursive: true);
    }

    Directory.CreateDirectory(path);
}

static void CopyFramework(string sourcePath, string destinationPath)
{
    if (!Directory.Exists(sourcePath))
    {
        throw new DirectoryNotFoundException(
            $"The WebAssembly framework publish directory is missing: {sourcePath}");
    }

    Directory.CreateDirectory(destinationPath);
    foreach (string sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
    {
        string extension = Path.GetExtension(sourceFile);
        if (extension is ".br" or ".gz")
        {
            continue;
        }

        string relativePath = Path.GetRelativePath(sourcePath, sourceFile);
        CopyRequiredFile(sourceFile, Path.Join(destinationPath, relativePath));
    }
}

static void CopyRequiredFile(string sourcePath, string destinationPath)
{
    if (!File.Exists(sourcePath))
    {
        throw new FileNotFoundException("A browser worker publish input is missing.", sourcePath);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
    File.Copy(sourcePath, destinationPath, overwrite: true);
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
            $"{executablePath} failed with exit code {process.ExitCode}.");
    }
}
