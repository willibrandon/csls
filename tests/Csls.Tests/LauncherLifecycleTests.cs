using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies launcher and managed-worker process lifetime through real standard streams.
/// </summary>
[TestClass]
public sealed class LauncherLifecycleTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Terminates the launcher and worker when the LSP client process exits unexpectedly.
    /// </summary>
    [TestMethod]
    public async Task ClientProcessExitTerminatesLauncherAndWorker()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        Assert.IsTrue(File.Exists(launcherPath), $"Launcher not found at {launcherPath}.");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-launcher-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Program.cs"),
                """Console.WriteLine("lifecycle");""",
                TestContext.CancellationToken).ConfigureAwait(false);

            string processHostPath = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
            var clientStartInfo = new ProcessStartInfo
            {
                FileName = EditorToolResolver.ResolveDotNetHost(),
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            clientStartInfo.ArgumentList.Add(processHostPath);
            clientStartInfo.ArgumentList.Add("--wait-for-standard-input");
            using Process clientProcess = Process.Start(clientStartInfo)
                ?? throw new InvalidOperationException("The lifecycle client process did not start.");

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-launcher-lifecycle",
                EditorToolResolver.ResolveDotNetHost(),
                [launcherPath, "lsp"],
                fixturePath,
                environmentVariables: new Dictionary<string, string>
                {
                    ["CSLS_WORKER_PATH"] = workerPath
                }).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            await lsp.InitializeWithProcessIdAsync(
                fixturePath,
                clientProcess.Id,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            clientProcess.Kill(entireProcessTree: true);
            await clientProcess.WaitForExitAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);

            string diagnostics = await lsp.WaitForExitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.DoesNotContain(
                "Unhandled exception",
                diagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels a blocked progressive workspace load when the LSP transport disconnects.
    /// </summary>
    [TestMethod]
    public async Task TransportDisconnectCancelsBlockedProgressiveWorkspaceLoad()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string launcherPath = EditorToolResolver.ResolveLauncher(repositoryRoot);
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        string processHostPath = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
        Assert.IsTrue(File.Exists(launcherPath), $"Launcher not found at {launcherPath}.");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(
            File.Exists(processHostPath),
            $"Test process host not found at {processHostPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-progressive-disconnect-{Guid.NewGuid():N}");
        string buildStartedPath = Path.Join(fixturePath, "build-started");
        string buildReleasePath = Path.Join(fixturePath, "build-release");
        Directory.CreateDirectory(fixturePath);
        try
        {
            await WriteProjectAsync(
                Path.Join(fixturePath, "alpha"),
                "Alpha",
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await WriteProjectAsync(
                Path.Join(fixturePath, "beta"),
                "Beta",
                CreateBlockedProjectText(
                    processHostPath,
                    buildStartedPath,
                    buildReleasePath),
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-progressive-disconnect",
                EditorToolResolver.ResolveDotNetHost(),
                [launcherPath, "lsp"],
                fixturePath,
                environmentVariables: new Dictionary<string, string>
                {
                    ["CSLS_WORKER_PATH"] = workerPath
                }).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await FileTextWaiter.WaitAsync(
                buildStartedPath,
                "started",
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);

            await lsp.DisconnectInputAsync().ConfigureAwait(false);
            string diagnostics = await lsp.WaitForExitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.DoesNotContain(
                "Unhandled exception",
                diagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            await File.WriteAllTextAsync(
                buildReleasePath,
                "release",
                CancellationToken.None).ConfigureAwait(false);
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task WriteProjectAsync(
        string directoryPath,
        string projectName,
        string projectText,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directoryPath);
        await File.WriteAllTextAsync(
            Path.Join(directoryPath, $"{projectName}.csproj"),
            projectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(directoryPath, $"{projectName}.slnx"),
            $"<Solution><Project Path=\"{projectName}.csproj\" /></Solution>",
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(directoryPath, "Program.cs"),
            "Console.WriteLine(\"lifecycle\");",
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateBlockedProjectText(
        string processHostPath,
        string buildStartedPath,
        string buildReleasePath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Target Name="BlockDesignTimeBuild"
                      BeforeTargets="Compile"
                      Condition="'$(DesignTimeBuild)' == 'true'">
                <WriteLinesToFile File="{{buildStartedPath}}"
                                  Lines="started"
                                  Overwrite="true" />
                <Exec Command="&quot;{{dotnetPath}}&quot; &quot;{{processHostPath}}&quot; --wait-for-file &quot;{{buildReleasePath}}&quot;" />
              </Target>
            </Project>
            """;
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;
}
