namespace Csls.Debugger.Tests;

/// <summary>
/// Retains runtime crash stacks from test-owned Unix processes without capturing their memory.
/// </summary>
internal sealed class DebuggerCrashReportCapture : IAsyncDisposable
{
    private readonly TestContext _testContext;

    /// <summary>
    /// Creates an isolated report destination and child-process runtime configuration.
    /// </summary>
    /// <param name="testContext">The test that owns the processes and retained reports.</param>
    internal DebuggerCrashReportCapture(TestContext testContext)
    {
        _testContext = testContext;
        DirectoryPath = Directory.CreateTempSubdirectory("csls-debugger-crash-report-").FullName;
        Dictionary<string, string?> variables = [];
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            variables["DOTNET_DbgEnableMiniDump"] = "1";
            variables["DOTNET_EnableCrashReportOnly"] = "1";
            variables["DOTNET_DbgMiniDumpName"] = Path.Join(DirectoryPath, "process-%p");
        }

        Variables = variables;
    }

    /// <summary>
    /// Gets the test-owned runtime report directory.
    /// </summary>
    internal string DirectoryPath { get; }

    /// <summary>
    /// Gets configuration applied only to processes started by this test.
    /// </summary>
    internal IReadOnlyDictionary<string, string?> Variables { get; }

    /// <summary>
    /// Retains completed crash reports after the owned processes exit and removes the capture directory.
    /// </summary>
    /// <returns>A task that completes after report retention and directory cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (string report in Directory.EnumerateFiles(DirectoryPath, "*.crashreport.json"))
            {
                string results = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results");
                Directory.CreateDirectory(results);
                string artifact = Path.Join(results, $"native-crash-{Guid.NewGuid():N}.crashreport.json");
                File.Move(report, artifact);
                _testContext.AddResultFile(artifact);
                _testContext.WriteLine($"Runtime crash report retained at {artifact}.");
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(DirectoryPath, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }
}
