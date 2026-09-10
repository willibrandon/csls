using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Verifies required workflow summaries reject every unsuccessful dependency result.
/// </summary>
[TestClass]
public sealed class WorkflowDependencyVerifierTests
{
    /// <summary>
    /// Gets framework-managed cancellation for the real file-app build and executions.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Accepts success and fails closed for failed, cancelled, skipped, missing, and unknown results.
    /// </summary>
    [TestMethod]
    [Timeout(90000, CooperativeCancellation = true)]
    public async Task RequiredWorkflowSummaryAcceptsOnlySuccess()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string fixturePath = Path.Join(Path.GetTempPath(), $"csls-workflow-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string outputPath = Path.Join(fixturePath, "output");
            (int buildExit, string buildOutput, string buildError) = await RunAsync(repositoryRoot,
                ["build", Path.Join(repositoryRoot, "scripts", "Verify-WorkflowDependency.cs"),
                    "--artifacts-path", Path.Join(fixturePath, "artifacts"), "--output", outputPath,
                    "/bl:" + Path.Join(EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                        "binlogs", "workflow-dependency-test-{}.binlog")], result: null).ConfigureAwait(false);
            Assert.AreEqual(0, buildExit, buildOutput + buildError);
            string executable = Path.Join(outputPath, "Verify-WorkflowDependency.dll");
            foreach (string? result in new string?[] { "success", "failure", "cancelled", "skipped", null, "", "unknown" })
            {
                (int exitCode, string output, string error) = await RunAsync(repositoryRoot, [executable], result).ConfigureAwait(false);
                Assert.AreEqual(result == "success" ? 0 : 1, exitCode, result + ": " + output + error);
                if (result == "success")
                {
                    Assert.AreEqual("Workflow dependency completed successfully." + Environment.NewLine, output);
                    Assert.AreEqual(string.Empty, error);
                }
                else
                {
                    Assert.AreEqual(string.Empty, output);
                    Assert.Contains("expected 'success'", error);
                    Assert.Contains(result ?? "missing", error);
                }
            }

            (int helpExit, string helpOutput, string helpError) = await RunAsync(repositoryRoot, [executable, "--help"], result: null)
                .ConfigureAwait(false);
            Assert.AreEqual(0, helpExit, helpError);
            Assert.Contains("Usage:", helpOutput);
            Assert.AreEqual(string.Empty, helpError);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string repositoryRoot, IReadOnlyList<string> arguments, string? result)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["CSLS_WORKFLOW_DEPENDENCY_RESULT"] = result;
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("The workflow verifier failed to start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            return (process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
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
}
