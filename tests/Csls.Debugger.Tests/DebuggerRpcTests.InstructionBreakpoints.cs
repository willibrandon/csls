using Csls.Debugger.Contracts;
using Csls.Debugger.Control;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies private-RPC managed-IL breakpoint validation.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    private static async Task AssertRpcInstructionBreakpointValidationAsync(
        DebuggerRpcClient client,
        string instructionReference,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DebugInstructionBreakpointInfo> invalid = await client
            .SetInstructionBreakpointsAsync(
                new DebugInstructionBreakpointSetRequest(
                    [new DebugInstructionBreakpointRequest(
                        instructionReference,
                        long.MaxValue)]),
                cancellationToken).ConfigureAwait(false);
        Assert.HasCount(1, invalid);
        Assert.IsFalse(invalid[0].Verified);
        Assert.Contains(
            "outside",
            invalid[0].Message!,
            StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<DebugInstructionBreakpointInfo> cleared = await client
            .SetInstructionBreakpointsAsync(
                new DebugInstructionBreakpointSetRequest([]),
                cancellationToken).ConfigureAwait(false);
        Assert.HasCount(0, cleared);
    }
}
