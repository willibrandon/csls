using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded DAP framing through the public host and a real operating-system pipe.
/// </summary>
[TestClass]
public sealed class DapMessageReaderTests
{
    /// <summary>
    /// Reassembles a request whose header and payload arrive one byte at a time.
    /// </summary>
    [TestMethod]
    public async Task FragmentedOperatingSystemPipeRequestIsReassembled()
    {
        const string Payload = "{\"seq\":7,\"type\":\"request\",\"command\":\"initialize\"}";
        DapTestClient client = await DapTestClient
            .CreateAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);

        await client
            .SendFrameAsync(CreateFrame(Payload), fragment: true, CancellationToken.None)
            .ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Assert.AreEqual("response", response.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(7, response.RootElement.GetProperty("request_seq").GetInt32());
        Assert.AreEqual("initialize", response.RootElement.GetProperty("command").GetString());
        Assert.IsTrue(response.RootElement.GetProperty("success").GetBoolean());
    }

    /// <summary>
    /// Rejects duplicate content lengths before allocating the payload.
    /// </summary>
    [TestMethod]
    public async Task DuplicateContentLengthIsRejected()
    {
        byte[] frame = Encoding.ASCII.GetBytes(
            "Content-Length: 2\r\nContent-Length: 2\r\n\r\n");

        string diagnostics = await ReadInvalidFrameAsync(frame).ConfigureAwait(false);

        Assert.Contains("duplicate", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rejects a framed message without its required content length.
    /// </summary>
    [TestMethod]
    public async Task MissingContentLengthIsRejected()
    {
        byte[] frame = Encoding.ASCII.GetBytes("Content-Type: application/json\r\n\r\n");

        string diagnostics = await ReadInvalidFrameAsync(frame).ConfigureAwait(false);

        Assert.Contains("missing", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Content-Length", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects malformed JSON after reading the exact declared payload.
    /// </summary>
    [TestMethod]
    public async Task InvalidJsonPayloadIsRejected()
    {
        string diagnostics = await ReadInvalidFrameAsync(CreateFrame("{"))
            .ConfigureAwait(false);

        Assert.Contains("invalid JSON", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects syntactically valid JSON that is not a DAP request envelope.
    /// </summary>
    [TestMethod]
    public async Task InvalidRequestEnvelopeIsRejected()
    {
        string diagnostics = await ReadInvalidFrameAsync(CreateFrame("{}"))
            .ConfigureAwait(false);

        Assert.Contains("envelope", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects non-ASCII header bytes at the protocol boundary.
    /// </summary>
    [TestMethod]
    public async Task NonAsciiHeaderIsRejected()
    {
        byte[] frame = [0xc3, 0xa9];

        string diagnostics = await ReadInvalidFrameAsync(frame).ConfigureAwait(false);

        Assert.Contains("ASCII", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects a declared payload beyond the configured maximum.
    /// </summary>
    [TestMethod]
    public async Task OversizedPayloadIsRejected()
    {
        byte[] frame = Encoding.ASCII.GetBytes("Content-Length: 16777217\r\n\r\n");

        string diagnostics = await ReadInvalidFrameAsync(frame).ConfigureAwait(false);

        Assert.Contains("outside", diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects a payload that ends before its declared length.
    /// </summary>
    [TestMethod]
    public async Task TruncatedPayloadIsRejected()
    {
        byte[] frame = Encoding.ASCII.GetBytes("Content-Length: 8\r\n\r\n{}");
        DapTestClient client = await DapTestClient
            .CreateAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        await client
            .SendFrameAsync(frame, fragment: false, CancellationToken.None)
            .ConfigureAwait(false);
        await client.CloseProtocolAsync().ConfigureAwait(false);
        Assert.AreEqual(1, await client.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false));

        Assert.Contains(
            "truncated",
            client.Diagnostics.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateFrame(string payload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] header = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"Content-Length: {payloadBytes.Length}\r\n\r\n"));
        return [.. header, .. payloadBytes];
    }

    private static async Task<string> ReadInvalidFrameAsync(byte[] frame)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        await client
            .SendFrameAsync(frame, fragment: false, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(1, await client.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false));
        return client.Diagnostics.ToString();
    }
}
