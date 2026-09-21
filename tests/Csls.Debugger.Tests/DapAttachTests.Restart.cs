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
        DapTestClient? client = null;
        string stage = "target readiness";
        try
        {
            char[] readyBuffer = new char[5];
            int readyCount = await target.StandardOutput
                .ReadBlockAsync(readyBuffer, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(readyBuffer.Length, readyCount);
            Assert.AreEqual("ready", new string(readyBuffer));

            stage = "adapter initialization";
            client = await DapTestClient
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
            AssertResponse(initialize.RootElement, initializeSequence, "initialize");
            stage = "attach configuration";
            int attachSequence = await client.SendRequestAsync(
                "attach",
                writer => WriteAttachArguments(writer, target.Id),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");
            stage = "target attachment";
            int configurationSequence = await client.SendRequestAsync(
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
            AssertResponse(configuration.RootElement, configurationSequence, "configurationDone");
            AssertResponse(attach.RootElement, attachSequence, "attach");
            AssertEvent(process.RootElement, "process");

            for (int iteration = 0; iteration < 8; iteration++)
            {
                stage = $"restart attachment {iteration + 1}";
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
            }

            stage = "disconnect";
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
            stage = "detached target exit";
            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            if (!target.HasExited)
            {
                await target.WaitForExitAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            }
            Assert.AreEqual(0, target.ExitCode);
        }
        catch
        {
            TestContext.WriteLine($"Reattach stage: {stage}. Target PID: {target.Id}.");
            TestContext.WriteLine(client?.ProtocolTranscript ?? "The adapter has not started.");
            TestContext.WriteLine(client?.Diagnostics.ToString() ?? string.Empty);
            throw;
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }
            await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            File.Delete(waitPath);
        }
    }

    private static void WriteAttachArguments(Utf8JsonWriter writer, int processId,
        string? buildPath = null, string? localPath = null, bool? requireExactSource = null)
    {
        writer.WriteStartObject();
        writer.WriteNumber("processId", processId);
        if (requireExactSource is bool exactSource)
        {
            writer.WriteBoolean("requireExactSource", exactSource);
        }

        writer.WriteStartObject("sourceFileMap");
        writer.WriteString("/_/", FindRepositoryRoot());
        if (buildPath is not null && localPath is not null)
        {
            writer.WriteString(buildPath, localPath);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
