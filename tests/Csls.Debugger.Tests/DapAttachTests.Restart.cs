using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP re-attachment without taking process ownership.
/// </summary>
public sealed partial class DapAttachTests
{
    /// <summary>
    /// Detaches and reattaches the same running target during a DAP restart.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task RestartReattachesWithoutTerminatingTarget()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-reattach-{Guid.NewGuid():N}.signal");
        using Process target = StartManagedTarget(waitPath);
        try
        {
            char[] readyBuffer = new char[5];
            int readyCount = await target.StandardOutput
                .ReadBlockAsync(readyBuffer, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(readyBuffer.Length, readyCount);

            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "attach",
                writer => WriteAttachArguments(writer, target.Id),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument attach = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument process = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(process.RootElement, "process");

            int restartSequence = await client.SendRequestAsync(
                "restart",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("arguments");
                    WriteAttachArguments(writer, target.Id);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument restart = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument restartedProcess = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(restart.RootElement, restartSequence, "restart");
            AssertEvent(restartedProcess.RootElement, "process");
            Assert.AreEqual(
                target.Id,
                restartedProcess.RootElement.GetProperty("body")
                    .GetProperty("systemProcessId").GetInt32());
            Assert.IsFalse(target.HasExited);

            int disconnectSequence = await client.SendRequestAsync(
                "disconnect",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument disconnect = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(disconnect.RootElement, disconnectSequence, "disconnect");
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.IsFalse(target.HasExited);
        }
        finally
        {
            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            if (!target.HasExited)
            {
                await target.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }

            File.Delete(waitPath);
        }
    }

    private static void WriteAttachArguments(Utf8JsonWriter writer, int processId)
    {
        writer.WriteStartObject();
        writer.WriteNumber("processId", processId);
        writer.WriteStartObject("sourceFileMap");
        writer.WriteString("/_/", FindRepositoryRoot());
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
