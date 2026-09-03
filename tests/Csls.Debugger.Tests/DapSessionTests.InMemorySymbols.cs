using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies runtime-supplied in-memory Portable PDB behavior through DAP.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Binds source breakpoints from Assembly.Load PE and Portable PDB byte arrays.
    /// </summary>
    [TestMethod]
    public async Task InMemoryPortablePdbBindsSourceBreakpoint()
    {
        string repositoryRoot = FindRepositoryRoot();
        string fixtureDirectory = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Debugger.Fixtures.InMemory",
            "debug");
        string assemblyPath = Path.Join(
            fixtureDirectory,
            "Csls.Debugger.Fixtures.InMemory.dll");
        string symbolPath = Path.ChangeExtension(assemblyPath, ".pdb");
        string sourcePath = Path.Join(
            repositoryRoot,
            "test-assets",
            "Csls.Debugger.Fixtures.InMemory",
            "InMemoryFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains("answer++;", StringComparison.Ordinal))
            .Number;
        int waitLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains(
                "while (!File.Exists(signalPath))",
                StringComparison.Ordinal))
            .Number;
        string signalPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-in-memory-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
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
                    ["--debugger-in-memory-fixture", assemblyPath, symbolPath, signalPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            int threadId = await ConfigureBreakpointAsync(
                client,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            Assert.IsGreaterThan(0, threadId);
            await AssertInMemoryFrameAsync(
                client,
                threadId,
                sourcePath,
                breakpointLine,
                waitLine).ConfigureAwait(false);

            int modulesSequence = await client.SendRequestAsync(
                "modules",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument modules = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(modules.RootElement, modulesSequence, "modules", success: true);
            string?[] symbolStatuses = [.. modules.RootElement
                .GetProperty("body")
                .GetProperty("modules")
                .EnumerateArray()
                .Select(static module => module.GetProperty("symbolStatus").GetString())];
            Assert.Contains("In-memory Portable PDB loaded.", symbolStatuses);
            await DisconnectAsync(client).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(signalPath);
        }
    }

    private async Task AssertInMemoryFrameAsync(
        DapTestClient client,
        int threadId,
        string sourcePath,
        int breakpointLine,
        int waitLine)
    {
        int stackSequence = await client.SendRequestAsync(
            "stackTrace",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument stack = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(stack.RootElement, stackSequence, "stackTrace", success: true);
        JsonElement frame = stack.RootElement
            .GetProperty("body")
            .GetProperty("stackFrames")
            .EnumerateArray()
            .Single(candidate => candidate.TryGetProperty("source", out JsonElement source) &&
                DebuggerTestPath.AreEquivalent(
                    source.GetProperty("path").GetString(),
                    sourcePath));
        Assert.AreEqual(breakpointLine, frame.GetProperty("line").GetInt32());
        Assert.AreEqual(
            "Csls.Debugger.Fixtures.InMemory.InMemoryFixture.WaitForSignal",
            frame.GetProperty("name").GetString());
        string instructionReference = frame
            .GetProperty("instructionPointerReference")
            .GetString()!;
        Assert.StartsWith("csls-il-", instructionReference);

        int frameId = frame.GetProperty("id").GetInt32();
        int scopesSequence = await client.SendRequestAsync(
            "scopes",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("frameId", frameId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument scopes = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
        int variablesReference = scopes.RootElement
            .GetProperty("body")
            .GetProperty("scopes")
            .EnumerateArray()
            .Single(scope => scope.GetProperty("name").GetString() == "Locals")
            .GetProperty("variablesReference")
            .GetInt32();
        int variablesSequence = await client.SendRequestAsync(
            "variables",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("variablesReference", variablesReference);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument variables = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(variables.RootElement, variablesSequence, "variables", success: true);
        string?[] names = [.. variables.RootElement
            .GetProperty("body")
            .GetProperty("variables")
            .EnumerateArray()
            .Select(static variable => variable.GetProperty("name").GetString())];
        Assert.Contains("answer", names);

        JsonElement[] instructions = await ReadDisassemblyAsync(
            client,
            instructionReference,
            offset: 0,
            instructionOffset: 0,
            instructionCount: 8).ConfigureAwait(false);
        Assert.IsNotEmpty(instructions.Where(instruction =>
            instruction.TryGetProperty("instructionBytes", out _)).ToArray());
        Assert.IsNotEmpty(instructions.Where(instruction =>
            instruction.TryGetProperty("location", out JsonElement location) &&
            DebuggerTestPath.AreEquivalent(
                location.GetProperty("path").GetString(),
                sourcePath)).ToArray());

        JsonElement instructionBreakpoint = await SetInstructionBreakpointAsync(
            client,
            instructionReference,
            offset: 0).ConfigureAwait(false);
        Assert.IsTrue(instructionBreakpoint.GetProperty("verified").GetBoolean());
        await ClearInstructionBreakpointsAsync(client).ConfigureAwait(false);

        _ = await ReadGotoTargetAsync(client, sourcePath, waitLine).ConfigureAwait(false);
    }
}
