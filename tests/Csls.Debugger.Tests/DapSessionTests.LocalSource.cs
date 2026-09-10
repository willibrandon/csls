using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies local source-file validation through the production debugger transport.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Keeps source inspection responsive when a mapped source path is a named pipe without a writer.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MappedSourceFifoPreservesStoppedInspection()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-source-fifo-");
        string sourcePath = Path.Join(directory.FullName, "DebuggerFixture.cs");
        try
        {
            var command = new System.Diagnostics.ProcessStartInfo("mkfifo") { ArgumentList = { sourcePath } };
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                command, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, output + error);
            string program = ResolveTestProcessHost();
            using DebugSymbolReader symbols = DebugSymbolReader.TryOpen(program)
                ?? throw new AssertFailedException("The debugger fixture has no symbols.");
            ManagedSymbolDocument document = Assert.ContainsSingle(symbols.GetDocuments().Where(item =>
                item.Path.EndsWith("/DebuggerFixture.cs", StringComparison.Ordinal)));
            Assert.IsNotNull(document.Checksum);
            Assert.IsNull(document.EmbeddedSource);
            string originalSource = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerFixture.cs");
            int line = FindSourceLine(await File.ReadAllLinesAsync(originalSource, TestContext.CancellationToken)
                .ConfigureAwait(false), "Console.Write(announcement);");

            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("program", program);
                writer.WriteStartArray("args");
                writer.WriteStringValue("--debugger-fixture");
                writer.WriteStringValue(Path.Join(directory.FullName, "continue.signal"));
                writer.WriteEndArray();
                writer.WriteStartObject("sourceFileMap");
                writer.WriteString(document.Path, sourcePath);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int breakpoint = await client.SendRequestAsync("setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, sourcePath, line), TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, breakpoint, "setBreakpoints", success: true);
            }

            int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            int thread = await ReadInitialBreakpointStopAsync(client, configuration, launch, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int stack = await client.SendRequestAsync("stackTrace", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", thread);
                writer.WriteNumber("levels", 1);
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, stack, "stackTrace", success: true);
                JsonElement frame = Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("stackFrames").EnumerateArray());
                Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
                JsonElement source = frame.GetProperty("source");
                Assert.IsFalse(source.TryGetProperty("path", out _));
                if (document.SourceLinkUri is null)
                {
                    Assert.Contains("unavailable", source.GetProperty("origin").GetString()!);
                }
                else
                {
                    Assert.AreEqual("Source Link", source.GetProperty("origin").GetString());
                    Assert.IsGreaterThan(0, source.GetProperty("sourceReference").GetInt32());
                }
            }

            int threads = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, threads, "threads", success: true);
                Assert.Contains(thread, response.RootElement.GetProperty("body").GetProperty("threads")
                    .EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
            }

            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            File.Delete(sourcePath);
            directory.Delete();
        }
    }
}
