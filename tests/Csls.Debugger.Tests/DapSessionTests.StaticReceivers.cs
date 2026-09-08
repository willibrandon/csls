using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Preserves source value precedence when a loaded runtime type could also name a member receiver.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Keeps null local failures from executing static calls or publishing another receiver's completions.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NullNamespaceShadowPreservesReceiverIdentity()
    {
        string source = Path.Join(FindRepositoryRoot(), "test-assets", "Csls.Debugger.Fixtures.CSharp", "DebuggerStaticReceiverFixture.cs");
        int line = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false),
            "Console.Write(arguments[0]);");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }
        _ = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
            DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.CSharp", "Debug"), ["--static-receiver"],
            wait: true, noDebug: false, suppressJitOptimizations: true), TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }
        int threadId = await ConfigureBreakpointAsync(client, source, line).ConfigureAwait(false);
        int frameId = await AssertStoppedFrameAsync(client, threadId, source, line).ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, frameId, "System", "null", "object").ConfigureAwait(false);
        foreach (string expression in new[] { "System.Math.Abs(-42)", "System.Int32.MaxValue" })
        {
            JsonElement rejected = await ReadEvaluationAsync(client, frameId, expression, success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("null", message, StringComparison.OrdinalIgnoreCase);
            Assert.AreEqual(frameId, await AssertStoppedFrameAsync(client, threadId, source, line).ConfigureAwait(false));
        }
        int completion = await client.SendRequestAsync("completions", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("frameId", frameId);
            writer.WriteString("text", "System.Math.Ab");
            writer.WriteNumber("column", 15);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, completion, "completions", success: false);
            string? message = response.RootElement.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("null", message, StringComparison.OrdinalIgnoreCase);
        }
        await AssertStructAssignmentEvaluationAsync(client, frameId, "sentinel", "42", "int").ConfigureAwait(false);
        Assert.AreEqual(frameId, await AssertStoppedFrameAsync(client, threadId, source, line).ConfigureAwait(false));
        await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}
