using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Verifies prerequisite command validation through the real file-app process.
/// </summary>
[TestClass]
public sealed class GraphicalPrerequisiteCommandTests
{
    private static string? s_buildRoot;
    private static string? s_applicationPath;

    /// <summary>
    /// Gets the framework-managed cancellation token for each process invocation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds one immutable application before parallel command processes are started.
    /// </summary>
    [ClassInitialize]
    public static async Task BuildApplicationAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        s_buildRoot = Directory.CreateTempSubdirectory("csls-prerequisite-build-").FullName;
        string outputPath = Path.Join(s_buildRoot, "app");
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveAbsoluteDotNetHost(),
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList =
            {
                "build", Path.Join(repositoryRoot, "scripts", "Install-GraphicalEditorTestPrerequisites.cs"),
                "--artifacts-path", s_buildRoot, "--output", outputPath,
                $"-bl:{Path.Join(s_buildRoot, "build.binlog")}"
            }
        };
        try
        {
            (int exitCode, string output, string error) = await RunProcessAsync(startInfo, context.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, $"{output}{error}");
            s_applicationPath = Path.Join(outputPath, "Install-GraphicalEditorTestPrerequisites.dll");
            Assert.IsTrue(File.Exists(s_applicationPath), "The prerequisite application was not built.");
        }
        catch
        {
            RemoveBuildArtifacts();
            throw;
        }
    }

    /// <summary>
    /// Removes the class-owned build after every command process has exited.
    /// </summary>
    [ClassCleanup]
    public static void RemoveBuildArtifacts()
    {
        if (s_buildRoot is { } buildRoot)
        {
            s_buildRoot = null;
            s_applicationPath = null;
            Directory.Delete(buildRoot, recursive: true);
        }
    }

    /// <summary>
    /// Shows every provisioning option without running any installation command.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HelpDescribesProvisioningWithoutSideEffects()
    {
        string[] optionNames =
        [
            "--with-web-browsers", "--web-only", "--web-browser", "--without-clipboard",
            "--without-tree-sitter", "--without-vulkan", "--portable-packages",
            "--write-portable-cache-key", "--package-cache", "--download-only",
            "--write-package-cache-key", "--refresh-package-index"
        ];
        foreach (string help in new[] { "--help", "-h", "-?" })
        {
            (int exitCode, string output, string error) = await RunAsync([help]).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, error);
            Assert.AreEqual(string.Empty, error);
            Assert.Contains("Usage:", output, StringComparison.Ordinal);
            foreach (string option in optionNames)
            {
                Assert.Contains(option, output, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// Rejects malformed and incompatible options before invoking a package manager.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task InvalidArgumentsFailBeforeProvisioning()
    {
        (string[] Arguments, string Error)[] cases =
        [
            (["--unknown"], "--unknown"),
            (["--web-browser"], "--web-browser"),
            (["--web-browser", "invalid-browser"], "invalid-browser"),
            (["--web-only=invalid"], "--web-only"),
            (["--with-web-browsers=invalid"], "Unrecognized command or argument 'invalid'."),
            (["--package-cache"], "caching requires --web-only"),
            (["--web-only", "--web-browser", "chromium", "--package-cache"], "--package-cache"),
            (["--web-only"], "--web-only requires"),
            (["--package-cache", "unused-cache"], "caching requires --web-only"),
            (["--refresh-package-index"], "caching requires --web-only"),
            (["--write-package-cache-key"], "caching requires --web-only"),
            (["--web-only", "--web-browser", "chromium", "--download-only"],
                "--download-only requires --package-cache"),
            (["--web-only", "--web-browser", "chromium", "--portable-packages", "--package-cache", "unused-cache"],
                "cannot use portable packages"),
            (["--web-only", "--web-browser", "chromium", "--write-portable-cache-key", "--write-package-cache-key"],
                "cannot use portable packages"),
            (["--web-only", "--web-browser", "chromium", "--download-only", "--package-cache", "unused-cache",
                "--write-package-cache-key"], "cannot use --write-package-cache-key")
        ];
        foreach ((string[] arguments, string expectedError) in cases)
        {
            (int exitCode, string output, string error) = await RunAsync(arguments).ConfigureAwait(false);
            Assert.AreEqual(2, exitCode, $"{string.Join(' ', arguments)}: {output}{error}");
            Assert.Contains(expectedError, error, StringComparison.Ordinal);
            Assert.DoesNotContain("Starting ", error, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Accepts repeated browser selections and reaches cache planning without installing packages.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RepeatedBrowserSelectionsReachCachePlanning()
    {
        string[][] selections =
        [
            ["--with-web-browsers"],
            ["--web-browser", "chromium", "--web-browser", "firefox", "--web-browser", "webkit"],
            ["--web-browser", "chromium", "--web-browser", "chromium"]
        ];
        string expectedError = File.Exists("/etc/debian_version")
            ? "GITHUB_OUTPUT is required to write the portable package cache key."
            : "Automatic graphical editor test provisioning supports Debian and Ubuntu.";
        foreach (string[] selection in selections)
        {
            (int exitCode, string output, string error) = await RunAsync(
                ["--web-only", .. selection, "--write-portable-cache-key"]).ConfigureAwait(false);
            Assert.AreEqual(1, exitCode, $"{output}{error}");
            Assert.AreEqual(string.Empty, output);
            Assert.AreEqual(expectedError + Environment.NewLine, error);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] arguments)
    {
        string operationRoot = Directory.CreateTempSubdirectory("csls-prerequisite-command-").FullName;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = EditorToolResolver.ResolveAbsoluteDotNetHost(),
                WorkingDirectory = operationRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList =
                {
                    s_applicationPath ?? throw new InvalidOperationException("The prerequisite application was not built.")
                }
            };
            startInfo.Environment.Remove("GITHUB_OUTPUT");
            startInfo.Environment.Remove("GITHUB_ENV");
            startInfo.Environment.Remove("GITHUB_PATH");
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            (int exitCode, string output, string error) = await RunProcessAsync(startInfo, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(operationRoot),
                "Help and invalid arguments must not create package caches or provisioning files.");
            return (exitCode, output, error);
        }
        finally
        {
            Directory.Delete(operationRoot, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The prerequisite process did not start.");
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
