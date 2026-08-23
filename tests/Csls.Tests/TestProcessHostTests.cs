using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Verifies the real child-process isolation host used by editor tests.
/// </summary>
[TestClass]
public sealed class TestProcessHostTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Propagates an explicit environment override through the hosted process boundary.
    /// </summary>
    [TestMethod]
    public async Task EnvironmentOverrideReachesHostedProcess()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string processHostPath = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        string expectedValue = $"csls-{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in new[]
        {
            processHostPath,
            "--environment",
            "CSLS_PROCESS_HOST_PROBE",
            expectedValue,
            "--",
            dotnetPath,
            processHostPath,
            "--print-environment",
            "CSLS_PROCESS_HOST_PROBE"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The test process host did not start.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);

        Assert.AreEqual(0, process.ExitCode, standardError);
        Assert.AreEqual(expectedValue, standardOutput);
    }
}
