using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Provides focused DAP assertions for module and source-symbol discovery.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task AssertEmbeddedModuleAsync(DapTestClient client, string programPath)
    {
        int sequence = await client.SendRequestAsync(
            "modules",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "modules", success: true);
        JsonElement module = response.RootElement.GetProperty("body").GetProperty("modules")
            .EnumerateArray()
            .Single(candidate => candidate.TryGetProperty("path", out JsonElement path) &&
                string.Equals(path.GetString(), programPath, StringComparison.Ordinal));
        Assert.AreEqual(
            "Embedded Portable PDB loaded.",
            module.GetProperty("symbolStatus").GetString());
        Assert.IsFalse(module.TryGetProperty("symbolFilePath", out _));
    }

    private async Task<int> AssertLoadedSourceAsync(DapTestClient client, string sourcePath)
    {
        int sequence = await client.SendRequestAsync(
            "loadedSources",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "loadedSources", success: true);
        JsonElement source = response.RootElement.GetProperty("body").GetProperty("sources")
            .EnumerateArray()
            .Single(candidate => string.Equals(
                candidate.GetProperty("path").GetString(),
                sourcePath,
                StringComparison.Ordinal));
        Assert.AreEqual(Path.GetFileName(sourcePath), source.GetProperty("name").GetString());
        Assert.AreEqual("embedded source", source.GetProperty("origin").GetString());
        int sourceReference = source.GetProperty("sourceReference").GetInt32();
        Assert.IsGreaterThan(0, sourceReference);
        JsonElement checksum = source.GetProperty("checksums")[0];
        Assert.AreEqual("SHA256", checksum.GetProperty("algorithm").GetString());
        Assert.HasCount(64, checksum.GetProperty("checksum").GetString()!);
        return sourceReference;
    }

    private async Task AssertBreakpointLocationAsync(
        DapTestClient client,
        string sourcePath,
        int zeroBasedLine)
    {
        int sequence = await client.SendRequestAsync(
            "breakpointLocations",
            writer => WriteBreakpointLocationsArguments(writer, sourcePath, zeroBasedLine),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "breakpointLocations", success: true);
        JsonElement[] locations = [.. response.RootElement.GetProperty("body")
            .GetProperty("breakpoints")
            .EnumerateArray()];
        Assert.IsNotEmpty(locations);
        Assert.Contains(
            zeroBasedLine,
            locations.Select(static location => location.GetProperty("line").GetInt32()));
        JsonElement matchingLocation = locations.First(
            location => location.GetProperty("line").GetInt32() == zeroBasedLine);
        Assert.IsGreaterThanOrEqualTo(
            0,
            matchingLocation.GetProperty("column").GetInt32());
    }
}
