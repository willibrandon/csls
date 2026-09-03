using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies recovery of runtime-owned in-memory symbols during attach.
/// </summary>
public sealed partial class DapAttachTests
{
    /// <summary>
    /// Resolves frames and locals for an assembly loaded from bytes before debugger attachment.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task AttachRecoversInMemoryPortablePdb()
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
        int waitStartLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains(
                "while (!File.Exists(signalPath))",
                StringComparison.Ordinal))
            .Number;
        int waitEndLine = sourceLines
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(candidate => candidate.Line.Contains(
                "Thread.SpinWait(10_000);",
                StringComparison.Ordinal))
            .Number;
        string signalPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-in-memory-attach-{Guid.NewGuid():N}.signal");
        using Process target = StartInMemoryTarget(
            assemblyPath,
            symbolPath,
            signalPath);
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
            await AttachAsync(client, target.Id).ConfigureAwait(false);
            await WaitForInMemoryModuleAsync(client).ConfigureAwait(false);
            (int threadId, JsonDocument stopped, JsonDocument pause) =
                await PauseAsync(client).ConfigureAwait(false);
            using (stopped)
            using (pause)
            {
                await AssertAttachedInMemoryFrameAsync(
                    client,
                    threadId,
                    sourcePath,
                    waitStartLine,
                    waitEndLine).ConfigureAwait(false);
            }
            int disconnectSequence = await client.SendRequestAsync(
                "disconnect",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument disconnect = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(disconnect.RootElement, disconnectSequence, "disconnect");
            Assert.IsFalse(target.HasExited);

            await File.WriteAllTextAsync(
                signalPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, target.ExitCode);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
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

            File.Delete(signalPath);
        }
    }

    private async Task AttachAsync(DapTestClient client, int processId)
    {
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
            writer => WriteAttachArguments(writer, processId),
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
    }

    private async Task<(int ThreadId, JsonDocument Stopped, JsonDocument Pause)> PauseAsync(
        DapTestClient client)
    {
        int pauseSequence = await client.SendRequestAsync(
            "pause",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonDocument stopped = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(stopped.RootElement, "stopped");
        int threadId = stopped.RootElement.GetProperty("body")
            .GetProperty("threadId").GetInt32();
        JsonDocument pause = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(pause.RootElement, pauseSequence, "pause");
        return (threadId, stopped, pause);
    }

    private async Task AssertAttachedInMemoryFrameAsync(
        DapTestClient client,
        int stoppedThreadId,
        string sourcePath,
        int waitStartLine,
        int waitEndLine)
    {
        int threadsSequence = await client.SendRequestAsync(
            "threads",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads");
        int[] threadIds = [stoppedThreadId, .. threads.RootElement.GetProperty("body")
            .GetProperty("threads").EnumerateArray()
            .Select(static thread => thread.GetProperty("id").GetInt32())
            .Where(threadId => threadId != stoppedThreadId)];
        JsonElement? selectedFrame = null;
        var stackResponses = new List<string>();
        foreach (int threadId in threadIds)
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
            AssertResponse(stack.RootElement, stackSequence, "stackTrace");
            selectedFrame = stack.RootElement.GetProperty("body")
                .GetProperty("stackFrames")
                .EnumerateArray()
                .Select(static frame => (JsonElement?)frame.Clone())
                .SingleOrDefault(candidate => candidate is JsonElement frame &&
                    frame.TryGetProperty("source", out JsonElement source) &&
                    DebuggerTestPath.AreEquivalent(
                        source.GetProperty("path").GetString(),
                        sourcePath));
            if (selectedFrame is not null)
            {
                break;
            }

            stackResponses.Add(stack.RootElement.ToString());
        }

        Assert.IsNotNull(
            selectedFrame,
            $"No attached thread exposed the in-memory source frame. {string.Join(' ', stackResponses)}");
        JsonElement frame = selectedFrame
            ?? throw new InvalidOperationException(
                "No attached thread exposed the in-memory source frame.");
        int frameLine = frame.GetProperty("line").GetInt32();
        Assert.IsGreaterThanOrEqualTo(waitStartLine, frameLine);
        Assert.IsLessThanOrEqualTo(waitEndLine, frameLine);
        Assert.AreEqual(
            "Csls.Debugger.Fixtures.InMemory.InMemoryFixture.WaitForSignal",
            frame.GetProperty("name").GetString());
        await AssertAttachedLocalAsync(client, frame.GetProperty("id").GetInt32())
            .ConfigureAwait(false);
    }

    private async Task AssertAttachedLocalAsync(DapTestClient client, int frameId)
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
        AssertResponse(scopes.RootElement, scopesSequence, "scopes");
        int variablesReference = scopes.RootElement.GetProperty("body")
            .GetProperty("scopes").EnumerateArray()
            .Single(scope => scope.GetProperty("name").GetString() == "Locals")
            .GetProperty("variablesReference").GetInt32();
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
        AssertResponse(variables.RootElement, variablesSequence, "variables");
        Assert.IsNotEmpty(variables.RootElement.GetProperty("body")
            .GetProperty("variables").EnumerateArray()
            .Where(variable => variable.GetProperty("name").GetString() == "answer")
            .ToArray());
    }

    private async Task WaitForInMemoryModuleAsync(DapTestClient client)
    {
        string? lastResponse = null;
        for (int attempt = 0; attempt < 250; attempt++)
        {
            int sequence = await client.SendRequestAsync(
                "modules",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument response = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(response.RootElement, sequence, "modules");
            if (response.RootElement.GetProperty("body")
                .GetProperty("modules").EnumerateArray()
                .Any(module => module.GetProperty("symbolStatus").GetString() ==
                    "In-memory Portable PDB loaded."))
            {
                return;
            }

            lastResponse = response.RootElement.ToString();
            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                TestContext.CancellationToken).ConfigureAwait(false);
        }

        Assert.Fail($"The attached in-memory module was not discovered. {lastResponse}");
    }

    private static Process StartInMemoryTarget(
        string assemblyPath,
        string symbolPath,
        string signalPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ResolveTestProcessHost());
        startInfo.ArgumentList.Add("--debugger-in-memory-attach-fixture");
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add(symbolPath);
        startInfo.ArgumentList.Add(signalPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The in-memory attach fixture did not start.");
    }
}
