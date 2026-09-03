using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies function-breakpoint configuration over private debugger control RPC.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    /// <summary>
    /// Replaces and normalizes function breakpoints through a real Unix socket.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task PrivateRpcReplacesManagedFunctionBreakpoints()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-rpc-function-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
                .StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable workerDisposal = worker.ConfigureAwait(false);
            DebuggerRpcClient client = worker.Client;

            IReadOnlyList<DebugFunctionBreakpointInfo> pending = await client
                .SetFunctionBreakpointsAsync(
                    new DebugFunctionBreakpointSetRequest(
                        [new DebugFunctionBreakpointRequest("Example.Type.Method(string)")]),
                    TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.HasCount(1, pending);
            Assert.AreEqual("Example.Type.Method", pending[0].Name);
            Assert.IsFalse(pending[0].Verified);
            Assert.IsNotNull(pending[0].Message);

            IReadOnlyList<DebugFunctionBreakpointInfo> cleared = await client
                .SetFunctionBreakpointsAsync(
                    new DebugFunctionBreakpointSetRequest([]),
                    TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsEmpty(cleared);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
