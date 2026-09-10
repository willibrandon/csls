using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies strict validation of DAP symbol search settings.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rejects an unsafe authenticated symbol-server URL before starting a target.
    /// </summary>
    [TestMethod]
    public async Task SymbolOptionsRejectAuthenticatedServerUrl()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int sequence = await client.SendRequestAsync(
            "launch",
            writer => WriteInvalidSymbolOptionsLaunch(writer),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "launch", success: false);
        Assert.Contains(
            "anonymous HTTP(S) base URLs",
            response.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
        await client.CloseProtocolAsync().ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private static void WriteInvalidSymbolOptionsLaunch(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("program", ResolveTestProcessHost());
        writer.WriteStartObject("symbolOptions");
        writer.WriteStartArray("searchPaths");
        writer.WriteStringValue("https://user:secret@example.test/symbols");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
