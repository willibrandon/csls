using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native fault artifacts retain references from each root sharing an address window.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeFaultMemoryCaptureTests : DapTestContext
{
    /// <summary>
    /// Reads owned native allocations through the process handle and preserves their links in either root order.
    /// </summary>
    /// <param name="descending">Whether the first root follows the linked root in the same window.</param>
    /// <param name="leadingReferences">Whether earlier links consume the first root's reference budget.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public unsafe void SharedWindowRootsRetainReferencedStorage(bool descending, bool leadingReferences)
    {
        byte* root = (byte*)NativeMemory.AlignedAlloc(65536, 65536);
        try
        {
            byte* leaf = (byte*)NativeMemory.AlignedAlloc(65536, 65536);
            try
            {
                NativeMemory.Clear(root, 65536);
                NativeMemory.Clear(leaf, 65536);
                byte[] identity = Guid.NewGuid().ToByteArray();
                identity.CopyTo(new Span<byte>(leaf, identity.Length));
                *(nuint*)(root + 4096) = (nuint)leaf;
                if (leadingReferences)
                {
                    for (int index = 0; index < 32; index++)
                    {
                        ((nuint*)root)[index] = (nuint)(root + 16384 + index * sizeof(nuint));
                    }
                }
                nuint[] roots = [(nuint)(root + (descending ? 8192 : 0)), (nuint)(root + 4096)];
                using var process = Process.GetCurrentProcess();
                string path = WindowsNativeFaultMemoryCapture.Write(process, [], roots, TestContext);
                using FileStream artifact = File.OpenRead(path);
                using var document = JsonDocument.Parse(artifact);
                JsonElement[] regions = [.. document.RootElement.GetProperty("regions").EnumerateArray()];
                Assert.IsLessThan(3L * 1024 * 1024, artifact.Length);
                Assert.IsInRange(1, 32, regions.Length);
                ulong rootAddress = (ulong)root;
                ulong leafAddress = (ulong)leaf;
                _ = Assert.ContainsSingle(regions.Where(region => region.GetProperty("address").GetUInt64() == rootAddress));
                JsonElement retained = Assert.ContainsSingle(regions.Where(region =>
                    leafAddress >= region.GetProperty("address").GetUInt64() &&
                    leafAddress - region.GetProperty("address").GetUInt64() + (ulong)identity.Length <=
                        (ulong)region.GetProperty("bytes").GetBytesFromBase64().Length));
                byte[] bytes = retained.GetProperty("bytes").GetBytesFromBase64();
                int offset = checked((int)(leafAddress - retained.GetProperty("address").GetUInt64()));
                Assert.AreSequenceEqual(identity, bytes.AsSpan(offset, identity.Length).ToArray());
                Assert.AreEqual(0, retained.GetProperty("readError").GetInt32());
            }
            finally
            {
                NativeMemory.AlignedFree(leaf);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(root);
        }
    }
}
