namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies compiler-facing capabilities for each supported CoreCLR generation.
/// </summary>
[TestClass]
public sealed class HotReloadRuntimeCapabilitiesTests
{
    /// <summary>
    /// Exposes only capabilities implemented by the exact target generation.
    /// </summary>
    [TestMethod]
    public void RuntimeGenerationControlsAdvertisedCapabilities()
    {
        IReadOnlyList<string> unavailable = HotReloadRuntimeCapabilities.Get(null);
        IReadOnlyList<string> net5 = HotReloadRuntimeCapabilities.Get(new Version(5, 0));
        IReadOnlyList<string> net6 = HotReloadRuntimeCapabilities.Get(new Version(6, 0));
        IReadOnlyList<string> net8 = HotReloadRuntimeCapabilities.Get(new Version(8, 0));
        IReadOnlyList<string> net10 = HotReloadRuntimeCapabilities.Get(new Version(10, 0));

        Assert.IsEmpty(unavailable);
        Assert.Contains("Baseline", net5);
        Assert.DoesNotContain("ChangeCustomAttributes", net5);
        Assert.Contains("ChangeCustomAttributes", net6);
        Assert.DoesNotContain("GenericUpdateMethod", net6);
        Assert.Contains("GenericUpdateMethod", net8);
        Assert.DoesNotContain("AddFieldRva", net8);
        Assert.Contains("AddFieldRva", net10);
    }
}
