using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies executable entry stops across symbol formats through real DAP transports.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops at the managed entry IL when the executable was compiled without symbols.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StopAtEntryUsesManagedEntryWithoutSymbols()
    {
        string program = SymbolFixtures.SymbolFreeProgramPath;
        using (FileStream image = File.OpenRead(program))
        using (var pe = new PEReader(image))
        {
            Assert.IsFalse(pe.ReadDebugDirectory().Any(static entry =>
                entry.Type is DebugDirectoryEntryType.CodeView or DebugDirectoryEntryType.EmbeddedPortablePdb));
        }

        string signal = Path.Join(Path.GetTempPath(), $"csls-entry-symbol-free-{Guid.NewGuid():N}.signal");
        try
        {
            await File.WriteAllTextAsync(signal, "release", TestContext.CancellationToken).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            (int threadId, _) = await LaunchAtEntryAsync(client, program, [signal, "41", "entry-result"])
                .ConfigureAwait(false);
            int sequence = await client.SendRequestAsync("stackTrace", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteNumber("levels", 1);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stack = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(stack.RootElement, sequence, "stackTrace", success: true);
            JsonElement frame = Assert.ContainsSingle(stack.RootElement.GetProperty("body")
                .GetProperty("stackFrames").EnumerateArray());
            Assert.AreEqual("Csls.Debugger.Fixtures.CSharp.Program.Main", frame.GetProperty("name").GetString());
            Assert.IsFalse(frame.TryGetProperty("source", out _));
            string reference = frame.GetProperty("instructionPointerReference").GetString()
                ?? throw new AssertFailedException("The symbol-free entry omitted its IL reference.");
            JsonElement[] instructions = await ReadDisassemblyAsync(client, reference, offset: 0,
                instructionOffset: 0, instructionCount: 1).ConfigureAwait(false);
            Assert.AreEqual("nop", Assert.ContainsSingle(instructions).GetProperty("instruction").GetString());
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        }
        finally
        {
            File.Delete(signal);
        }
    }

    /// <summary>
    /// Uses the compiler-authored entry source recorded in a Windows PDB.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Include, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StopAtEntryUsesWindowsPdbSource()
    {
        string program = SymbolFixtures.WindowsPdbProgramPath
            ?? throw new AssertFailedException("The Windows PDB fixture is unavailable.");
        string source = SymbolFixtures.SourcePath;
        int expectedLine = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken)
            .ConfigureAwait(false), "internal static int Main(string[] arguments)") + 1;
        string signal = Path.Join(Path.GetTempPath(), $"csls-entry-windows-pdb-{Guid.NewGuid():N}.signal");
        try
        {
            await File.WriteAllTextAsync(signal, "release", TestContext.CancellationToken).ConfigureAwait(false);
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            (int threadId, _) = await LaunchAtEntryAsync(client, program, [signal, "41", "entry-result"])
                .ConfigureAwait(false);
            (_, string? path, int line) = await ReadSourceFrameAsync(
                client, threadId, source, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(source, path);
            Assert.AreEqual(expectedLine, line);
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
        }
        finally
        {
            File.Delete(signal);
        }
    }
}
