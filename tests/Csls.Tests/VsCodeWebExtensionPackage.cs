using System.Diagnostics;
using System.IO.Compression;

namespace Csls.Tests;

/// <summary>
/// Builds and extracts one shared web VSIX for the real browser-host tests.
/// </summary>
internal static class VsCodeWebExtensionPackage
{
    private static readonly Lock s_gate = new();
    private static Task<string>? s_packageTask;

    /// <summary>
    /// Gets the extracted web extension built for the current test process.
    /// </summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The path to the extracted web extension.</returns>
    internal static Task<string> GetAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        Task<string> packageTask;
        lock (s_gate)
        {
            s_packageTask ??= VsCodeExtensionBuildGate.RunAsync(
                () => PackageAsync(repositoryRoot));
            packageTask = s_packageTask;
        }

        return packageTask.WaitAsync(cancellationToken);
    }

    private static async Task<string> PackageAsync(string repositoryRoot)
    {
        string outputDirectory = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "vscode-web-test-extension");
        string? configuredPackagePath = Environment.GetEnvironmentVariable(
            "CSLS_VSCODE_WEB_PACKAGE_PATH");
        string packagePath = string.IsNullOrWhiteSpace(configuredPackagePath)
            ? Path.Join(outputDirectory, "willibrandon.csls-web.vsix")
            : Path.GetFullPath(configuredPackagePath);
        string extractionPath = Path.Join(outputDirectory, "extracted");
        Directory.CreateDirectory(outputDirectory);
        if (string.IsNullOrWhiteSpace(configuredPackagePath))
        {
            using Process process = StartPackageProcess(repositoryRoot, packagePath);
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromMinutes(3))
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
                    The VS Code web extension packager failed with exit code {process.ExitCode}.
                    Standard output:
                    {output}
                    Standard error:
                    {error}
                    """);
            }
        }

        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(
                "The VS Code web extension packager did not create its output.",
                packagePath);
        }

        if (Directory.Exists(extractionPath))
        {
            await DirectoryReleaseWaiter.DeleteAsync(extractionPath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        await ZipFile.ExtractToDirectoryAsync(
            packagePath,
            extractionPath,
            CancellationToken.None).ConfigureAwait(false);
        string extensionPath = Path.Join(extractionPath, "extension");
        if (!File.Exists(Path.Join(extensionPath, "package.json")))
        {
            throw new InvalidDataException(
                "The VS Code web extension package has no extension manifest.");
        }

        RequirePackagedFile(extensionPath, "dist", "browserExtension.cjs");
        RequirePackagedFile(
            extensionPath,
            "dist",
            "browserServer",
            "cslsBrowserWorker.js");

        string testSuiteSource = Path.Join(
            repositoryRoot,
            "editors",
            "vscode",
            "dist",
            "test",
            "web-suite.cjs");
        if (!File.Exists(testSuiteSource))
        {
            throw new FileNotFoundException(
                "Build the VS Code web test suite before running its host tests.",
                testSuiteSource);
        }

        string testSuitePath = Path.Join(extensionPath, "dist", "test", "web-suite.cjs");
        Directory.CreateDirectory(Path.GetDirectoryName(testSuitePath)!);
        File.Copy(testSuiteSource, testSuitePath);

        return extensionPath;
    }

    private static void RequirePackagedFile(string extensionPath, params string[] segments)
    {
        string filePath = Path.Join([extensionPath, .. segments]);
        if (!File.Exists(filePath))
        {
            throw new InvalidDataException(
                $"The VS Code web extension package is missing {Path.GetRelativePath(extensionPath, filePath)}.");
        }
    }

    private static Process StartPackageProcess(string repositoryRoot, string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(Path.Join("scripts", "Build-VsCodeExtension.cs"));
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add("1.0.0");
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add("web");
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The VS Code web extension packager did not start.");
    }
}
