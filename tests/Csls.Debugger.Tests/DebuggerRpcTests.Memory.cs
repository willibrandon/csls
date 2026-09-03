using Csls.Debugger.Contracts;
using System.Buffers.Binary;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies private-RPC target-memory payloads.
/// </summary>
public sealed partial class DebuggerRpcTests
{
    private static void AssertRpcArrayMemory(DebugMemoryReadResult memory)
    {
        Assert.IsGreaterThan(0UL, memory.Address);
        Assert.AreEqual(0, memory.UnreadableBytes);
        Assert.HasCount(64, memory.Data.ToArray());
        ReadOnlySpan<byte> bytes = memory.Data.Span;
        for (int offset = 0; offset <= bytes.Length - 12; offset++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]) == 41 &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]) == 42 &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 8)..]) == 43)
            {
                return;
            }
        }

        Assert.Fail("The private RPC payload omitted the managed-array data.");
    }
}
