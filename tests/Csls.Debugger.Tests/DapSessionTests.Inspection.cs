using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies stopped-state managed thread, stack, scope, and variable inspection.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Pauses a live managed target, enumerates real runtime threads, and resumes execution.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedPauseEnumeratesThreadsAndContinuesTarget()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-pause-{Guid.NewGuid():N}.signal");
        try
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
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false),
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
            using JsonDocument ready = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(ready.RootElement, "output");
            Assert.AreEqual(
                "ready",
                ready.RootElement.GetProperty("body").GetProperty("output").GetString());

            int pauseSequence = await client.SendRequestAsync(
                "pause",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stopped = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(stopped.RootElement, "stopped");
            Assert.AreEqual(
                "pause",
                stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
            Assert.IsTrue(
                stopped.RootElement.GetProperty("body").GetProperty("allThreadsStopped").GetBoolean());
            using JsonDocument pause = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(pause.RootElement, pauseSequence, "pause", success: true);

            int threadsSequence = await client.SendRequestAsync(
                "threads",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument threads = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
            JsonElement[] threadItems = [.. threads.RootElement
                .GetProperty("body")
                .GetProperty("threads")
                .EnumerateArray()];
            Assert.IsNotEmpty(threadItems);
            Assert.IsTrue(threadItems.All(thread => thread.GetProperty("id").GetInt32() > 0));
            Assert.HasCount(
                threadItems.Length,
                threadItems.Select(thread => thread.GetProperty("id").GetInt32()).Distinct().ToArray());

            int fixtureFrameId = 0;
            foreach (JsonElement thread in threadItems)
            {
                int threadId = thread.GetProperty("id").GetInt32();
                int stackSequence = await client.SendRequestAsync(
                    "stackTrace",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("threadId", threadId);
                        writer.WriteNumber("startFrame", 0);
                        writer.WriteNumber("levels", 64);
                        writer.WriteEndObject();
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument stack = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(stack.RootElement, stackSequence, "stackTrace", success: true);
                JsonElement[] frames = [.. stack.RootElement
                    .GetProperty("body")
                    .GetProperty("stackFrames")
                    .EnumerateArray()];
                Assert.IsGreaterThanOrEqualTo(
                    frames.Length,
                    stack.RootElement.GetProperty("body").GetProperty("totalFrames").GetInt32());
                JsonElement fixtureFrame = frames.FirstOrDefault(frame =>
                    frame.TryGetProperty("source", out JsonElement source) &&
                    source.GetProperty("path").GetString() is string path &&
                    path.EndsWith(
                        Path.Join("tests", "Csls.TestProcessHost", "DebuggerFixture.cs"),
                        StringComparison.Ordinal) &&
                    frame.GetProperty("line").GetInt32() > 0);
                if (fixtureFrame.ValueKind != JsonValueKind.Undefined)
                {
                    fixtureFrameId = fixtureFrame.GetProperty("id").GetInt32();
                    break;
                }
            }

            Assert.IsGreaterThan(0, fixtureFrameId, "No managed stack frame resolved to the fixture source.");

            int scopesSequence = await client.SendRequestAsync(
                "scopes",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("frameId", fixtureFrameId);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument scopes = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
            JsonElement[] scopeItems = [.. scopes.RootElement
                .GetProperty("body")
                .GetProperty("scopes")
                .EnumerateArray()];
            Assert.HasCount(2, scopeItems);
            int staleVariablesReference = scopeItems[0]
                .GetProperty("variablesReference")
                .GetInt32();

            Dictionary<string, JsonElement[]> variablesByScope = new(StringComparer.Ordinal);
            foreach (JsonElement scope in scopeItems)
            {
                int reference = scope.GetProperty("variablesReference").GetInt32();
                int variablesSequence = await client.SendRequestAsync(
                    "variables",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("variablesReference", reference);
                        writer.WriteEndObject();
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument variables = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(variables.RootElement, variablesSequence, "variables", success: true);
                variablesByScope.Add(
                    scope.GetProperty("name").GetString()!,
                    [.. variables.RootElement
                        .GetProperty("body")
                        .GetProperty("variables")
                        .EnumerateArray()
                        .Select(variable => variable.Clone())]);
            }

            JsonElement[] arguments = variablesByScope["Arguments"];
            Dictionary<string, JsonElement> argumentsByName = arguments.ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("42", argumentsByName["number"].GetProperty("value").GetString());
            Assert.AreEqual("int", argumentsByName["number"].GetProperty("type").GetString());
            Assert.AreEqual(
                "\"answer\"",
                argumentsByName["text"].GetProperty("value").GetString());
            Assert.AreEqual("string", argumentsByName["text"].GetProperty("type").GetString());
            JsonElement[] locals = variablesByScope["Locals"];
            Dictionary<string, JsonElement> localsByName = locals.ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("43", localsByName["localNumber"].GetProperty("value").GetString());
            Assert.AreEqual(
                "\"answer!\"",
                localsByName["localText"].GetProperty("value").GetString());
            JsonElement localArray = localsByName["localArray"];
            int arrayReference = localArray.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, arrayReference);
            JsonElement[] arrayElements = await ReadVariablesAsync(client, arrayReference)
                .ConfigureAwait(false);
            Assert.HasCount(3, arrayElements);
            Assert.AreEqual("[0]", arrayElements[0].GetProperty("name").GetString());
            Assert.AreEqual("41", arrayElements[0].GetProperty("value").GetString());
            Assert.AreEqual("[2]", arrayElements[2].GetProperty("name").GetString());
            Assert.AreEqual("43", arrayElements[2].GetProperty("value").GetString());

            JsonElement localObject = localsByName["localObject"];
            int objectReference = localObject.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, objectReference);
            JsonElement[] fields = await ReadVariablesAsync(client, objectReference)
                .ConfigureAwait(false);
            Dictionary<string, JsonElement> fieldsByName = fields.ToDictionary(
                field => field.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("42", fieldsByName["Number"].GetProperty("value").GetString());
            Assert.AreEqual(
                "\"answer!\"",
                fieldsByName["Text"].GetProperty("value").GetString());

            int continueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument continued = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(continued.RootElement, "continued");
            using JsonDocument continueResponse = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                continueResponse.RootElement,
                continueSequence,
                "continue",
                success: true);

            int secondPauseSequence = await client.SendRequestAsync(
                "pause",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument secondStopped = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(secondStopped.RootElement, "stopped");
            using JsonDocument secondPause = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(secondPause.RootElement, secondPauseSequence, "pause", success: true);

            int staleVariablesSequence = await client.SendRequestAsync(
                "variables",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("variablesReference", staleVariablesReference);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument staleVariables = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                staleVariables.RootElement,
                staleVariablesSequence,
                "variables",
                success: false);
            Assert.Contains(
                "stale",
                staleVariables.RootElement.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);

            int secondContinueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument secondContinued = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(secondContinued.RootElement, "continued");
            using JsonDocument secondContinue = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                secondContinue.RootElement,
                secondContinueSequence,
                "continue",
                success: true);

            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument exited = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(exited.RootElement, "exited");
            using JsonDocument terminated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(terminated.RootElement, "terminated");
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

}
