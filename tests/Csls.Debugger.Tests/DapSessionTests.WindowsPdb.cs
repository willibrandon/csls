using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native Windows PDB behavior through a real compiler and DAP session.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Binds and inspects source from an identity-matched Windows PDB.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task WindowsPdbSupportsSourceAndInspectionRequests()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-windows-pdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            string programPath = SymbolFixtures.WindowsPdbProgramPath
                ?? throw new InvalidOperationException(
                    "The Windows PDB fixture is unavailable on Windows.");
            string symbolPath = Path.ChangeExtension(programPath, ".pdb");
            byte[] signature = new byte[24];
            using (FileStream symbols = File.OpenRead(symbolPath))
            {
                await symbols.ReadExactlyAsync(
                    signature,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            Assert.AreEqual(
                "Microsoft C/C++ MSF 7.00",
                Encoding.ASCII.GetString(signature));

            string sourcePath = SymbolFixtures.SourcePath;
            string[] sourceLines = await File.ReadAllLinesAsync(
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            int breakpointLine = FindSourceLine(sourceLines, "answer++;");
            int waitLine = FindSourceLine(sourceLines, "while (!File.Exists(arguments[0]))");
            string signalPath = Path.Join(testDirectory, "continue.signal");

            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await InitializeAndLaunchAsync(client, programPath, signalPath).ConfigureAwait(false);
            int threadId = await ConfigureBreakpointAsync(
                client,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            (int frameId, string instructionReference) = await AssertWindowsPdbFrameAsync(
                client,
                threadId,
                sourcePath,
                breakpointLine).ConfigureAwait(false);
            await AssertWindowsPdbEmbeddedSourceAsync(client, threadId).ConfigureAwait(false);
            await AssertWindowsPdbLocalsAsync(client, frameId).ConfigureAwait(false);
            await AssertWindowsPdbModuleAsync(client, programPath, symbolPath)
                .ConfigureAwait(false);

            JsonElement[] instructions = await ReadDisassemblyAsync(
                client,
                instructionReference,
                offset: 0,
                instructionOffset: 0,
                instructionCount: 8).ConfigureAwait(false);
            Assert.IsNotEmpty(instructions.Where(instruction =>
                instruction.TryGetProperty("location", out JsonElement location) &&
                DebuggerTestPath.AreEquivalent(
                    location.GetProperty("path").GetString(),
                    sourcePath))
                .ToArray());
            _ = await ReadGotoTargetAsync(client, sourcePath, waitLine).ConfigureAwait(false);

            await DisconnectAsync(client).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task<(int FrameId, string InstructionReference)> AssertWindowsPdbFrameAsync(
        DapTestClient client,
        int threadId,
        string sourcePath,
        int breakpointLine)
    {
        int sequence = await client.SendRequestAsync(
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
        AssertResponse(stack.RootElement, sequence, "stackTrace", success: true);
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
            "Csls.Debugger.Fixtures.CSharp.Program.Main",
            frame.GetProperty("name").GetString());
        return (
            frame.GetProperty("id").GetInt32(),
            frame.GetProperty("instructionPointerReference").GetString()!);
    }

    private async Task AssertWindowsPdbLocalsAsync(DapTestClient client, int frameId)
    {
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
    }

    private async Task AssertWindowsPdbEmbeddedSourceAsync(
        DapTestClient client,
        int threadId)
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
        JsonElement source = stack.RootElement
            .GetProperty("body")
            .GetProperty("stackFrames")
            .EnumerateArray()
            .First(frame => frame.TryGetProperty("source", out JsonElement candidate) &&
                candidate.TryGetProperty("origin", out JsonElement origin) &&
                origin.GetString() == "embedded source")
            .GetProperty("source");
        int sourceReference = source.GetProperty("sourceReference").GetInt32();
        Assert.IsGreaterThan(0, sourceReference);
        int sourceSequence = await client.SendRequestAsync(
            "source",
            writer => WriteSourceArguments(writer, sourceReference),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument content = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(content.RootElement, sourceSequence, "source", success: true);
        Assert.Contains(
            "answer++;",
            content.RootElement.GetProperty("body").GetProperty("content").GetString()!,
            StringComparison.Ordinal);
    }

    private async Task AssertWindowsPdbModuleAsync(
        DapTestClient client,
        string programPath,
        string symbolPath)
    {
        int sequence = await client.SendRequestAsync(
            "modules",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument modules = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(modules.RootElement, sequence, "modules", success: true);
        JsonElement module = modules.RootElement
            .GetProperty("body")
            .GetProperty("modules")
            .EnumerateArray()
            .Single(candidate => DebuggerTestPath.AreEquivalent(
                candidate.GetProperty("path").GetString()!,
                programPath));
        Assert.AreEqual("Windows PDB loaded.", module.GetProperty("symbolStatus").GetString());
        Assert.AreEqual(symbolPath, module.GetProperty("symbolFilePath").GetString());
    }
}
