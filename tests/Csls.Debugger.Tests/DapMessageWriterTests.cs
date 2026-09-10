using Csls.DebugAdapter;
using Csls.DebugAdapter.Protocol;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies complete DAP frame writes under cancellation and real pipe backpressure.
/// </summary>
[TestClass]
public sealed class DapMessageWriterTests
{
    /// <summary>
    /// Gets the test deadline used by every real pipe operation.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Keeps a completed source result when cancellation arrives before its response is written.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CompletedResponseSurvivesLateRequestCancellation()
    {
        string content = await File.ReadAllTextAsync(Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(),
            "src", "Csls.Debugger", "AssemblyInfo.cs"), TestContext.CancellationToken).ConfigureAwait(false);
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 4096);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, output.ClientSafePipeHandle);
        var writer = new DapMessageWriter(output, TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable writerDisposal = writer.ConfigureAwait(false);
        using var requestCancellation = new CancellationTokenSource();
        await requestCancellation.CancelAsync().ConfigureAwait(false);
        var request = new Request { Seq = 41, Type = "request", Command = "source" };
        await writer.WriteResponseAsync(request, success: true, message: null, json =>
        {
            json.WriteStartObject();
            json.WriteString("content", content);
            json.WriteString("mimeType", "text/x-csharp");
            json.WriteEndObject();
        }, requestCancellation.Token).ConfigureAwait(false);
        byte[] payload = new byte[await ReadHeaderAsync(input).ConfigureAwait(false)];
        await input.ReadExactlyAsync(payload, TestContext.CancellationToken).ConfigureAwait(false);
        using var response = JsonDocument.Parse(payload);
        Assert.AreEqual("response", response.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(41, response.RootElement.GetProperty("request_seq").GetInt32());
        Assert.AreEqual("source", response.RootElement.GetProperty("command").GetString());
        Assert.IsTrue(response.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual(content, response.RootElement.GetProperty("body").GetProperty("content").GetString());
    }

    /// <summary>
    /// Discards an event canceled before writing without leaving bytes in the next frame.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CanceledUnstartedEventLeavesConnectionUsable()
    {
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 4096);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, output.ClientSafePipeHandle);
        var writer = new DapMessageWriter(output, TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable writerDisposal = writer.ConfigureAwait(false);
        using var requestCancellation = new CancellationTokenSource();
        await requestCancellation.CancelAsync().ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => writer.WriteEventAsync(
            "output", json => WriteOutput(json, "unused"), requestCancellation.Token).AsTask()).ConfigureAwait(false);
        await writer.WriteEventAsync("terminated", writeBody: null, TestContext.CancellationToken).ConfigureAwait(false);
        byte[] payload = new byte[await ReadHeaderAsync(input).ConfigureAwait(false)];
        await input.ReadExactlyAsync(payload, TestContext.CancellationToken).ConfigureAwait(false);
        using var response = JsonDocument.Parse(payload);
        Assert.AreEqual("terminated", response.RootElement.GetProperty("event").GetString());
        Assert.AreEqual(1, response.RootElement.GetProperty("seq").GetInt32());
    }

    /// <summary>
    /// Finishes a started frame when its request is canceled and keeps the next frame readable.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RequestCancellationCannotTruncateStartedFrame()
    {
        string content = await ReadRuntimeContractAsync().ConfigureAwait(false);
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 4096);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, output.ClientSafePipeHandle);
        var writer = new DapMessageWriter(output, TestContext.CancellationToken);
        await using ConfiguredAsyncDisposable writerDisposal = writer.ConfigureAwait(false);
        using var requestCancellation = new CancellationTokenSource();
        Task writing = writer.WriteEventAsync("output", json => WriteOutput(json, content),
            requestCancellation.Token).AsTask();
        int length = await ReadHeaderAsync(input).ConfigureAwait(false);
        Assert.IsGreaterThan(4096, length);
        Assert.IsFalse(writing.IsCompleted, "The real pipe must apply backpressure before cancellation.");
        await requestCancellation.CancelAsync().ConfigureAwait(false);

        byte[] payload = new byte[length];
        using var readingCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        Task reading = input.ReadExactlyAsync(payload, readingCancellation.Token).AsTask();
        try
        {
            await writing.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await reading.ConfigureAwait(false);
        }
        finally
        {
            await readingCancellation.CancelAsync().ConfigureAwait(false);
            await SettleReadAsync(reading, readingCancellation.Token).ConfigureAwait(false);
        }

        using (var message = JsonDocument.Parse(payload))
        {
            Assert.AreEqual("event", message.RootElement.GetProperty("type").GetString());
            Assert.AreEqual("output", message.RootElement.GetProperty("event").GetString());
            Assert.AreEqual(1, message.RootElement.GetProperty("seq").GetInt32());
            Assert.AreEqual(content, message.RootElement.GetProperty("body").GetProperty("output").GetString());
        }

        await writer.WriteEventAsync("terminated", writeBody: null, TestContext.CancellationToken).ConfigureAwait(false);
        byte[] nextPayload = new byte[await ReadHeaderAsync(input).ConfigureAwait(false)];
        await input.ReadExactlyAsync(nextPayload, TestContext.CancellationToken).ConfigureAwait(false);
        using var next = JsonDocument.Parse(nextPayload);
        Assert.AreEqual("terminated", next.RootElement.GetProperty("event").GetString());
        Assert.AreEqual(2, next.RootElement.GetProperty("seq").GetInt32());
    }

    /// <summary>
    /// Stops a blocked frame when the complete connection is canceled.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ConnectionCancellationInterruptsBlockedFrame()
    {
        string content = await ReadRuntimeContractAsync().ConfigureAwait(false);
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 4096);
        using var input = new AnonymousPipeClientStream(PipeDirection.In, output.ClientSafePipeHandle);
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var writer = new DapMessageWriter(output, connectionCancellation.Token);
        await using ConfiguredAsyncDisposable writerDisposal = writer.ConfigureAwait(false);
        Task writing = writer.WriteEventAsync("output", json => WriteOutput(json, content),
            TestContext.CancellationToken).AsTask();
        int length = await ReadHeaderAsync(input).ConfigureAwait(false);
        Assert.IsGreaterThan(4096, length);
        Assert.IsFalse(writing.IsCompleted);
        await connectionCancellation.CancelAsync().ConfigureAwait(false);
        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            () => writing.WaitAsync(TestContext.CancellationToken)).ConfigureAwait(false);
    }

    private Task<string> ReadRuntimeContractAsync() => File.ReadAllTextAsync(Path.Join(
        DebuggerTestEnvironment.FindRepositoryRoot(), "src", "Csls.Debugger", "Interop", "cordebug.idl"),
        TestContext.CancellationToken);

    private static async Task SettleReadAsync(Task reading, CancellationToken cancellationToken)
    {
        try
        {
            await reading.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task<int> ReadHeaderAsync(Stream input)
    {
        List<byte> header = [];
        byte[] next = new byte[1];
        while (header.Count < 8192)
        {
            await input.ReadExactlyAsync(next, TestContext.CancellationToken).ConfigureAwait(false);
            header.Add(next[0]);
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n')
            {
                string text = Encoding.ASCII.GetString([.. header]);
                Assert.StartsWith("Content-Length: ", text);
                return int.Parse(text["Content-Length: ".Length..^4], CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidDataException("The DAP writer produced an oversized header.");
    }

    private static void WriteOutput(Utf8JsonWriter writer, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("category", "console");
        writer.WriteString("output", content);
        writer.WriteEndObject();
    }
}
