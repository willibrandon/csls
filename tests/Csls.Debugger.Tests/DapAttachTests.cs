using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger attachment and non-owning target lifecycle behavior.
/// </summary>
[TestClass]
public sealed partial class DapAttachTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Attaches to a real CoreCLR process and detaches without terminating it.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AttachPausesAndDisconnectLeavesTargetRunning()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-attach-{Guid.NewGuid():N}.signal");
        using Process target = StartManagedTarget(waitPath);
        try
        {
            char[] readyBuffer = new char[5];
            int readyCount = await target.StandardOutput
                .ReadBlockAsync(readyBuffer, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(readyBuffer.Length, readyCount);
            Assert.AreEqual("ready", new string(readyBuffer));

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
            AssertResponse(initialize.RootElement, initializeSequence, "initialize");

            int attachSequence = await client.SendRequestAsync(
                "attach",
                writer => WriteAttachArguments(writer, target.Id),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                configuration.RootElement,
                configurationSequence,
                "configurationDone");
            using JsonDocument attach = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(attach.RootElement, attachSequence, "attach");
            using JsonDocument process = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(process.RootElement, "process");
            JsonElement processBody = process.RootElement.GetProperty("body");
            Assert.AreEqual(target.Id, processBody.GetProperty("systemProcessId").GetInt32());
            Assert.AreEqual("attach", processBody.GetProperty("startMethod").GetString());

            int pauseSequence = await client.SendRequestAsync(
                "pause",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument pause = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(pause.RootElement, pauseSequence, "pause");
            using JsonDocument stopped = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(stopped.RootElement, "stopped");
            Assert.AreEqual(
                "pause",
                stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());

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
                await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsFalse(target.HasExited, "A default attach disconnect terminated the target.");

            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, target.ExitCode);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                await target.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }

            File.Delete(waitPath);
        }
    }

    private static Process StartManagedTarget(string waitPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ResolveTestProcessHost());
        startInfo.ArgumentList.Add("--announce-and-spin-until-file");
        startInfo.ArgumentList.Add(waitPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The managed attach fixture did not start.");
    }

    private static string ResolveTestProcessHost()
    {
        string repositoryRoot = FindRepositoryRoot();
        return Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.TestProcessHost",
            "debug",
            "csls-test-process-host.dll");
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
        => DebuggerTestEnvironment.FindRepositoryRoot(sourcePath);

    private static void WriteEmptyObject(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private static void AssertResponse(
        JsonElement message,
        int requestSequence,
        string command)
    {
        Assert.AreEqual("response", message.GetProperty("type").GetString());
        Assert.AreEqual(requestSequence, message.GetProperty("request_seq").GetInt32());
        Assert.AreEqual(command, message.GetProperty("command").GetString());
        Assert.IsTrue(message.GetProperty("success").GetBoolean(), message.ToString());
    }

    private static void AssertEvent(JsonElement message, string eventName)
    {
        Assert.AreEqual("event", message.GetProperty("type").GetString());
        Assert.AreEqual(eventName, message.GetProperty("event").GetString());
    }
}
