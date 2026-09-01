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
}
