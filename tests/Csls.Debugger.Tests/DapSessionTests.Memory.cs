using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
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

    /// <summary>
    /// Reports a failed fixture launch before pausing and releases the adapter owned by setup.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StoppedFixtureLaunchFailurePreservesErrorAndReleasesAdapter()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-fixture-startup-");
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            int adapterProcessId = client.HostProcessId;
            string missingProgram = Path.Join(directory.FullName, "missing.dll");
            Assert.IsFalse(File.Exists(missingProgram));
            AssertFailedException failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
                () => StartStoppedFixtureAsync(client, missingProgram, Path.Join(directory.FullName, "wait.signal")))
                .ConfigureAwait(false);
            Assert.Contains("The launch program does not exist.", failure.Message);
            Assert.DoesNotContain("\"command\":\"pause\"", client.ProtocolTranscript);
            await AssertProcessExitedAsync(adapterProcessId, TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            directory.Delete();
        }
    }

    private async Task<DapTestClient> StartStoppedFixtureAsync(
        string waitPath, bool blockForInspection = false, bool? showRawValues = null,
        bool? allowImplicitFuncEval = null)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        return await StartStoppedFixtureAsync(client, ResolveTestProcessHost(), waitPath,
            blockForInspection, showRawValues, allowImplicitFuncEval).ConfigureAwait(false);
    }

    private async Task<DapTestClient> StartStoppedFixtureAsync(
        DapTestClient client, string program, string waitPath, bool blockForInspection = false,
        bool? showRawValues = null, bool? allowImplicitFuncEval = null)
    {
        try
        {
            int initializeSequence = await client.SendRequestAsync(
                "initialize", WriteVariablePagingInitializeArguments, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument initialize = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
                Assert.IsTrue(initialize.RootElement.GetProperty("body")
                    .GetProperty("supportsReadMemoryRequest").GetBoolean());
            }

            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    program,
                    [blockForInspection ? "--debugger-unsafe-stop-fixture" : "--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false,
                    showRawValues: showRawValues,
                    allowImplicitFuncEval: allowImplicitFuncEval),
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument configuration = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(configuration.RootElement, configurationSequence, "configurationDone", success: true);
            }

            using (JsonDocument launch = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
            }

            using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(process.RootElement, "process");
                Assert.IsGreaterThan(0, process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32());
            }

            var output = new StringBuilder();
            while (output.Length < "ready".Length)
            {
                using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AppendFixtureOutput(message.RootElement, output);
            }

            Assert.AreEqual("ready", output.ToString());
            await PauseFixtureAsync(client).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
