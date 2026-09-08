namespace Csls.Debugger.Tests;

/// <summary>
/// Retains runtime crash reports and optional minidumps from test-owned Unix processes.
/// </summary>
internal sealed class DebuggerCrashReportCapture : IAsyncDisposable
{
    private readonly TestContext _testContext;

    /// <summary>
    /// Creates an isolated report destination and child-process runtime configuration.
    /// </summary>
    /// <param name="testContext">The test that owns the processes and retained reports.</param>
    /// <param name="captureMemory">Whether to retain a minidump alongside each runtime crash report.</param>
    internal DebuggerCrashReportCapture(TestContext testContext, bool captureMemory = false)
    {
        _testContext = testContext;
        ArtifactDirectory = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results",
            $"native-crash-{Guid.NewGuid():N}");
        DirectoryPath = Directory.CreateTempSubdirectory("csls-debugger-crash-report-").FullName;
        Dictionary<string, string?> variables = [];
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            variables["DOTNET_DbgEnableMiniDump"] = "1";
            variables["DOTNET_EnableCrashReport"] = "1";
            variables["DOTNET_EnableCrashReportOnly"] = captureMemory ? "0" : "1";
            variables["DOTNET_DbgMiniDumpType"] = "1";
            variables["DOTNET_DbgMiniDumpName"] = Path.Join(DirectoryPath, captureMemory ? "process-%p.dmp" : "process-%p");
        }

        Variables = variables;
    }

    /// <summary>
    /// Gets the test-owned runtime report directory.
    /// </summary>
    internal string DirectoryPath { get; }

    /// <summary>
    /// Gets the unique retained-artifact directory created when a child produces crash evidence.
    /// </summary>
    internal string ArtifactDirectory { get; }

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
            foreach (string report in Directory.EnumerateFiles(DirectoryPath).Where(path =>
                path.EndsWith(".crashreport.json", StringComparison.Ordinal) || path.EndsWith(".dmp", StringComparison.Ordinal)))
            {
                Directory.CreateDirectory(ArtifactDirectory);
                string artifact = Path.Join(ArtifactDirectory, Path.GetFileName(report));
                File.Move(report, artifact);
                _testContext.AddResultFile(artifact);
                _testContext.WriteLine($"Runtime crash artifact retained at {artifact}.");
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(DirectoryPath, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }
}
