using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies strict validation of DAP Just My Code settings.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rejects a non-boolean attach setting before opening a target process.
    /// </summary>
    [TestMethod]
    public async Task AttachJustMyCodeRejectsNonBooleanValue()
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
            "attach",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("processId", 1);
                writer.WriteString("justMyCode", "true");
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "attach", success: false);
        Assert.Contains(
            "target 'justMyCode' value must be a boolean",
            response.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
        await client.CloseProtocolAsync().ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
