using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies hit-condition validation through the real debug adapter transport.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reports an invalid hit condition as an unverified breakpoint without failing the request.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task InvalidHitConditionReturnsUnverifiedBreakpoint()
    {
        string signalPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-invalid-hit-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            int initializeSequence = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                initialize.RootElement,
                initializeSequence,
                "initialize",
                success: true);
            _ = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", signalPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");
            int breakpointSequence = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteHitSourceBreakpointArguments(
                    writer,
                    condition: null,
                    hitCondition: "not-a-hit-count"),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                response.RootElement,
                breakpointSequence,
                "setBreakpoints",
                success: true);
            JsonElement breakpoint = response.RootElement.GetProperty("body")
                .GetProperty("breakpoints")[0];
            Assert.IsFalse(breakpoint.GetProperty("verified").GetBoolean());
            Assert.Contains(
                "positive number",
                breakpoint.GetProperty("message").GetString()!,
                StringComparison.Ordinal);
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(signalPath);
        }
    }
}
