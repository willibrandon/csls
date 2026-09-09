using System.Runtime.CompilerServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Retains four GiB of independently allocated, populated arrays and a cyclic path for live debugger inspection.
/// </summary>
internal static class DebuggerLargeGraphFixture
{
    /// <summary>
    /// Allocates distinct backing storage before stopping at an authored source location.
    /// </summary>
    /// <returns>Zero after the inspected graph survives its owning test's observation interval.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static int Run()
    {
        const int ChunkCount = 64;
        const int ChunkLength = 64 * 1024 * 1024;
        byte[][] chunks = new byte[ChunkCount][];
        for (int index = 0; index < chunks.Length; index++)
        {
            byte[] chunk = new byte[ChunkLength];
            Array.Fill(chunk, checked((byte)(index + 1)));
            chunks[index] = chunk;
        }

        object[] cycle = new object[1];
        cycle[0] = cycle;
        object[] graph = [chunks, cycle];
        DebuggerBlockingWait.Wait("large-graph-ready");
        GC.KeepAlive(graph);
        return 0;
    }
}
