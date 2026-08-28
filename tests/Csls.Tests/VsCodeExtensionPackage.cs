using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Builds one shared VS Code extension package for the real extension-host tests.
/// </summary>
internal static class VsCodeExtensionPackage
{
    private static readonly Lock s_gate = new();
    private static Task<string>? s_packageTask;

    /// <summary>
    /// Gets the extension package built for the current test process.
    /// </summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The path to the shared extension package.</returns>
    internal static Task<string> GetAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        Task<string> packageTask;
        lock (s_gate)
        {
            s_packageTask ??= PackageAsync(repositoryRoot);
            packageTask = s_packageTask;
        }

        return packageTask.WaitAsync(cancellationToken);
    }

    private static async Task<string> PackageAsync(string repositoryRoot)
    {
        string extensionRoot = Path.Join(repositoryRoot, "editors", "vscode");
        string vscePath = Path.Join(
            extensionRoot,
            "node_modules",
            "@vscode",
            "vsce",
            "vsce");
        if (!File.Exists(vscePath))
        {
            throw new FileNotFoundException(
                "The VS Code extension is not provisioned. Run scripts/Provision-VsCode.cs.",
                vscePath);
        }

        string outputDirectory = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "vscode-test-extension");
        string outputPath = Path.Join(outputDirectory, "willibrandon.csls.vsix");
        Directory.CreateDirectory(outputDirectory);
        using Process process = StartPackageProcess(extensionRoot, vscePath, outputPath);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"""
                The VS Code extension packager failed with exit code {process.ExitCode}.
                Standard output:
                {output}
                Standard error:
                {error}
                """);
        }

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException(
                "The VS Code extension packager did not create its output.",
                outputPath);
        }

        return outputPath;
    }

    private static Process StartPackageProcess(
        string extensionRoot,
        string vscePath,
        string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = extensionRoot
        };
        startInfo.ArgumentList.Add(vscePath);
        startInfo.ArgumentList.Add("package");
        startInfo.ArgumentList.Add("--no-dependencies");
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(outputPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The VS Code extension packager did not start.");
    }
}
