using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        string stagingPath = Path.Join(outputDirectory, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            CopyRequiredFile(extensionRoot, stagingPath, "package.json");
            CopyRequiredFile(extensionRoot, stagingPath, "README.md");
            CopyRequiredFile(extensionRoot, stagingPath, "CHANGELOG.md");
            CopyRequiredFile(extensionRoot, stagingPath, "LICENSE");
            CopyRequiredFile(extensionRoot, stagingPath, "language-configuration.json");
            CopyRequiredFile(extensionRoot, stagingPath, ".vscodeignore");
            CopyRequiredFile(extensionRoot, stagingPath, "dist", "extension.cjs");
            CopyRequiredFile(extensionRoot, stagingPath, "media", "icon.png");
            CopyServer(repositoryRoot, stagingPath);
            await PrepareManifestAsync(Path.Join(stagingPath, "package.json"))
                .ConfigureAwait(false);

            using Process process = StartPackageProcess(stagingPath, vscePath, outputPath);
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
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException(
                "The VS Code extension packager did not create its output.",
                outputPath);
        }

        return outputPath;
    }

    private static void CopyServer(string repositoryRoot, string stagingPath)
    {
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        CopyDirectory(
            Path.GetDirectoryName(launcherPath)!,
            Path.Join(stagingPath, "server"));
        CopyDirectory(
            Path.GetDirectoryName(workerPath)!,
            Path.Join(stagingPath, "server", "workers", "server"));
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException(
                $"A VS Code test server input is missing: {sourcePath}");
        }

        foreach (string sourceFile in Directory.EnumerateFiles(
            sourcePath,
            "*",
            SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourcePath, sourceFile);
            string destinationFile = Path.Join(destinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile);
        }
    }

    private static void CopyRequiredFile(
        string sourceRoot,
        string destinationRoot,
        params string[] relativeSegments)
    {
        string sourcePath = Path.Join([sourceRoot, .. relativeSegments]);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "A VS Code test extension input is missing.",
                sourcePath);
        }

        string destinationPath = Path.Join([destinationRoot, .. relativeSegments]);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath);
    }

    private static async Task PrepareManifestAsync(string packagePath)
    {
        JsonNode package = JsonNode.Parse(
            await File.ReadAllTextAsync(packagePath).ConfigureAwait(false))
            ?? throw new InvalidDataException("The VS Code package manifest is empty.");
        package.AsObject().Remove("scripts");
        package.AsObject().Remove("devDependencies");
        package.AsObject().Remove("browser");
        package["capabilities"]?.AsObject().Remove("virtualWorkspaces");
        await File.WriteAllTextAsync(
            packagePath,
            package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n")
            .ConfigureAwait(false);
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
