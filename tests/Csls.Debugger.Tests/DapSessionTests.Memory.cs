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
            DapTestClient client = await StartStoppedFixtureAsync(waitPath).ConfigureAwait(false);
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
            string instructionReference = await AssertManagedFrameDisassemblyAsync(client)
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
            await AssertStaleDisassemblyRejectedAsync(client, instructionReference)
                .ConfigureAwait(false);

            await ResumeAndReleaseFixtureAsync(client, waitPath).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<DapTestClient> StartStoppedFixtureAsync(string waitPath)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteVariablePagingInitializeArguments,
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
        JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
        return await GetLocalArrayMemoryReferenceAsync(
            client,
            frame.GetProperty("id").GetInt32()).ConfigureAwait(false);
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
        AssertResponse(scopes.RootElement, sequence, "scopes", success: true);
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
