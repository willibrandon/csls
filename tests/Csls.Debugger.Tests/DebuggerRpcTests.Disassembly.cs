using Csls.Debugger.Contracts;
using Csls.Debugger.Control;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies private-RPC managed-IL payloads.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    private static async Task AssertRpcDisassemblyAsync(
        DebuggerRpcClient client,
        string instructionReference,
        CancellationToken cancellationToken)
    {
        DebugDisassembly disassembly = await client.DisassembleAsync(
            new DebugDisassemblyRequest(
                instructionReference,
                ByteOffset: 0,
                InstructionOffset: -4,
                InstructionCount: 12,
                ResolveSymbols: true),
            cancellationToken).ConfigureAwait(false);
        Assert.HasCount(12, disassembly.Instructions);
        Assert.IsNotEmpty(disassembly.Instructions.Where(instruction =>
            instruction.Instruction.Contains(
                "System.Threading.Thread.SpinWait",
                StringComparison.Ordinal)).ToArray());
        Assert.IsNotEmpty(disassembly.Instructions.Where(instruction =>
            instruction.Source is not null && instruction.Line > 0).ToArray());
        Assert.IsNotEmpty(disassembly.Instructions.Where(instruction =>
            !instruction.Bytes.IsEmpty).ToArray());
    }
}
