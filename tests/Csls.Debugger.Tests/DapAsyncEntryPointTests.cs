namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies async entry stopping through real managed launches and DAP streams.
/// </summary>
[TestClass]
public sealed class DapAsyncEntryPointTests : DapTestContext
{
    /// <summary>
    /// Enters the authored async top-level method before it produces target output.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public Task StopAtEntryUsesAsyncTopLevelSource() =>
        VerifyAsyncTopLevelEntryAsync(ResolveTestProcessHost());
}
