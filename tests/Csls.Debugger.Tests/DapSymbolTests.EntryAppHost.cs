namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native apphost entry stopping with the host-built symbol fixtures.
/// </summary>
public sealed partial class DapSymbolTests
{
    /// <summary>
    /// Enters the authored async top-level method through a native apphost.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task StopAtEntryUsesTopLevelAppHostSource() =>
        VerifyAsyncTopLevelEntryAsync(SymbolFixtures.EntryAppHostPath);
}
