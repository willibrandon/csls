using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP sequencing and target ownership through production sessions.
/// </summary>
[TestClass]
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Rejects invalid request ordering without preventing later initialization.
    /// </summary>
    [TestMethod]
    public async Task InvalidStateRequestDoesNotCorruptSession()
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int threadsSequence = await client.SendRequestAsync(
            "threads",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: false);
        Assert.Contains(
            "Created",
            threads.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);

        int repeatedSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument repeated = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(repeated.RootElement, repeatedSequence, "initialize", success: false);
        Assert.Contains(
            "Initialized",
            repeated.RootElement.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an unadvertised graceful-terminate request without ending the connection.
    /// </summary>
    [TestMethod]
    public async Task UnadvertisedTerminateRequestReturnsUnsupportedFailure()
    {
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
        int terminateSequence = await client.SendRequestAsync(
            "terminate",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument terminate = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);

        AssertResponse(terminate.RootElement, terminateSequence, "terminate", success: false);
        Assert.Contains(
            "not supported",
            terminate.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Terminates a real long-running target when its DAP owner disconnects.
    /// </summary>
    [TestMethod]
    public async Task DisconnectTerminatesRunningOwnedProcessTree()
    {
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
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--wait-for-standard-input"],
                wait: true),
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
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        int disconnectSequence = await client.SendRequestAsync(
            "disconnect",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonDocument message;
        do
        {
            message = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.TryGetProperty("request_seq", out JsonElement requestSequence) &&
                requestSequence.GetInt32() == disconnectSequence)
            {
                break;
            }

            message.Dispose();
        }
        while (true);
        using (message)
        {
            AssertResponse(message.RootElement, disconnectSequence, "disconnect", success: true);
        }

        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates an owned target when the adapter connection is canceled without disconnect.
    /// </summary>
    [TestMethod]
    public async Task ConnectionCancellationTerminatesRunningOwnedProcessTree()
    {
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
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--wait-for-standard-input"],
                wait: true),
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
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        await client.DisposeAsync().ConfigureAwait(false);

        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Terminates an owned target when the client closes its protocol stream abruptly.
    /// </summary>
    [TestMethod]
    public async Task EndOfStreamTerminatesRunningOwnedProcessTree()
    {
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
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--wait-for-standard-input"],
                wait: true),
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
        using JsonDocument launch = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument process = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();

        await client.CloseProtocolAsync().ConfigureAwait(false);

        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        await AssertProcessExitedAsync(processId, TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static void WriteLaunchArguments(
        Utf8JsonWriter writer,
        string processHost,
        IReadOnlyList<string> arguments,
        bool wait,
        bool noDebug = true)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", noDebug);
        writer.WriteString("program", processHost);
        writer.WriteStartArray("args");
        foreach (string argument in arguments)
        {
            writer.WriteStringValue(argument);
        }

        writer.WriteEndArray();
        if (!wait)
        {
            writer.WriteStartObject("env");
            writer.WriteString("CSLS_DEBUGGER_TEST_VALUE", "transport-value");
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteEmptyObject(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }

    private static void WriteSourceBreakpointArguments(
        Utf8JsonWriter writer,
        string sourcePath,
        int line)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("source");
        writer.WriteString("name", Path.GetFileName(sourcePath));
        writer.WriteString("path", sourcePath);
        writer.WriteEndObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteNumber("line", line);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void AssertResponse(
        JsonElement message,
        int requestSequence,
        string command,
        bool success)
    {
        Assert.AreEqual("response", message.GetProperty("type").GetString());
        Assert.AreEqual(requestSequence, message.GetProperty("request_seq").GetInt32());
        Assert.AreEqual(command, message.GetProperty("command").GetString());
        Assert.AreEqual(
            success,
            message.GetProperty("success").GetBoolean(),
            message.ToString());
    }

    private static void AssertEvent(JsonElement message, string eventName)
    {
        Assert.AreEqual("event", message.GetProperty("type").GetString());
        Assert.AreEqual(eventName, message.GetProperty("event").GetString());
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
    {
        DirectoryInfo? directory = new FileInfo(sourcePath).Directory;
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the csls repository root.");
    }

    private static async Task AssertProcessExitedAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }

        Assert.Fail($"Debugger-owned process {processId} remained alive after disconnect.");
    }
}
