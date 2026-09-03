using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded generation-owned memory inspection over a real DAP session.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reads a managed array and rejects its memory handle after execution resumes.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedArrayMemoryIsReadableOnlyForOwningStop()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-memory-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartMemoryFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            string memoryReference = await GetArrayMemoryReferenceAsync(client)
                .ConfigureAwait(false);
            Assert.StartsWith("csls-memory-", memoryReference);

            int readSequence = await client.SendRequestAsync(
                "readMemory",
                writer => WriteMemoryArguments(writer, memoryReference, 0, 64),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument read = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(read.RootElement, readSequence, "readMemory", success: true);
            JsonElement body = read.RootElement.GetProperty("body");
            Assert.StartsWith("0x", body.GetProperty("address").GetString()!);
            byte[] bytes = Convert.FromBase64String(body.GetProperty("data").GetString()!);
            Assert.HasCount(64, bytes);
            Assert.IsTrue(ContainsArrayValues(bytes), "The array payload was absent from target memory.");
            await AssertOversizedMemoryReadRejectedAsync(client, memoryReference)
                .ConfigureAwait(false);

            await ContinueAndPauseAsync(client).ConfigureAwait(false);
            int staleSequence = await client.SendRequestAsync(
                "readMemory",
                writer => WriteMemoryArguments(writer, memoryReference, 0, 1),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stale = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(stale.RootElement, staleSequence, "readMemory", success: false);
            Assert.Contains(
                "stale",
                stale.RootElement.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);

            await ResumeAndReleaseFixtureAsync(client, waitPath).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<DapTestClient> StartMemoryFixtureAsync(string waitPath)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.IsTrue(initialize.RootElement.GetProperty("body")
            .GetProperty("supportsReadMemoryRequest").GetBoolean());
        _ = await client.SendRequestAsync(
            "launch",
            writer => WriteLaunchArguments(
                writer,
                ResolveTestProcessHost(),
                ["--debugger-fixture", waitPath],
                wait: true,
                noDebug: false),
            TestContext.CancellationToken).ConfigureAwait(false);
        for (int index = 0; index < 5; index++)
        {
            using JsonDocument ignored = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (index == 0)
            {
                _ = await client.SendRequestAsync(
                    "configurationDone",
                    WriteEmptyObject,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }
        }

        await PauseFixtureAsync(client).ConfigureAwait(false);
        return client;
    }

    private async Task<string> GetArrayMemoryReferenceAsync(DapTestClient client)
    {
        int threadsSequence = await client.SendRequestAsync(
            "threads",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
        foreach (JsonElement thread in threads.RootElement.GetProperty("body")
            .GetProperty("threads").EnumerateArray())
        {
            int? frameId = await FindFixtureFrameAsync(
                client,
                thread.GetProperty("id").GetInt32()).ConfigureAwait(false);
            if (frameId is not null)
            {
                return await GetLocalArrayMemoryReferenceAsync(client, frameId.Value)
                    .ConfigureAwait(false);
            }
        }

        Assert.Fail("No managed stack frame resolved to the debugger fixture.");
        return string.Empty;
    }

    private async Task<int?> FindFixtureFrameAsync(DapTestClient client, int threadId)
    {
        int sequence = await client.SendRequestAsync(
            "stackTrace",
            writer => WriteStackArguments(writer, threadId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stackTrace", success: true);
        JsonElement frame = response.RootElement.GetProperty("body").GetProperty("stackFrames")
            .EnumerateArray().FirstOrDefault(candidate =>
                candidate.TryGetProperty("source", out JsonElement source) &&
                source.GetProperty("path").GetString() is string path &&
                path.EndsWith("DebuggerFixture.cs", StringComparison.Ordinal));
        return frame.ValueKind == JsonValueKind.Undefined
            ? null
            : frame.GetProperty("id").GetInt32();
    }

    private async Task<string> GetLocalArrayMemoryReferenceAsync(
        DapTestClient client,
        int frameId)
    {
        int sequence = await client.SendRequestAsync(
            "scopes",
            writer => WriteFrameArguments(writer, frameId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument scopes = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        JsonElement locals = scopes.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Single(scope => scope.GetProperty("name").GetString() == "Locals");
        JsonElement[] variables = await ReadVariablesAsync(
            client,
            locals.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
        return variables.Single(variable => variable.GetProperty("name").GetString() == "localArray")
            .GetProperty("memoryReference").GetString()!;
    }

    private static bool ContainsArrayValues(ReadOnlySpan<byte> bytes)
    {
        for (int offset = 0; offset <= bytes.Length - 12; offset++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]) == 41 &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 4)..]) == 42 &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(offset + 8)..]) == 43)
            {
                return true;
            }
        }

        return false;
    }
}
