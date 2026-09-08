using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Measures verified debugger operations against a checked-in executable target.
/// </summary>
internal static class DebuggerMeasurementSession
{
    /// <summary>
    /// Runs an independent session and returns measurements only after successful target and adapter shutdown.
    /// </summary>
    /// <param name="options">The selected binaries and sampling policy.</param>
    /// <param name="iteration">The one-based fresh-process iteration.</param>
    /// <param name="sourceLine">The authored breakpoint line.</param>
    /// <param name="cancellationToken">Cancels the session and retires its process tree.</param>
    /// <returns>The verified measurements and observed resources.</returns>
    internal static async Task<DebuggerPerformanceIteration> MeasureAsync(DebuggerPerformanceOptions options,
        int iteration, int sourceLine, CancellationToken cancellationToken)
    {
        var samples = new List<DebuggerPerformanceSample>();
        var resources = new List<DebuggerPerformanceResources>();
        DebuggerMeasurementClient client = await DebuggerMeasurementClient.StartAsync(options.ServerPath).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        JsonElement capabilities = await client.RequestAsync("initialize", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("clientID", "csls-debugger-performance");
            writer.WriteBoolean("linesStartAt1", true);
            writer.WriteBoolean("columnsStartAt1", true);
            writer.WriteBoolean("supportsVariablePaging", true);
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
        Require(capabilities.GetProperty("supportsConfigurationDoneRequest").GetBoolean(), "configurationDone capability");
        Record("dap/startup-initialize", "session", client.StartedTimestamp);

        long launchStarted = Stopwatch.GetTimestamp();
        int launch = await client.SendAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", options.TargetPath);
            writer.WriteBoolean("suppressJITOptimizations", true);
            writer.WriteStartArray("args");
            writer.WriteStringValue("debugger-target");
            writer.WriteEndArray();
            writer.WriteStartObject("sourceFileMap");
            writer.WriteString(Path.GetDirectoryName(DebuggerPerformanceTarget.SourcePath)
                ?? throw new InvalidOperationException("The compiled target requires a source directory."), Path.GetDirectoryName(options.SourcePath));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
        _ = await client.EventAsync("initialized", cancellationToken).ConfigureAwait(false);
        long bindingStarted = Stopwatch.GetTimestamp();
        JsonElement pending = await client.RequestAsync("setBreakpoints", writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartObject("source");
            writer.WriteString("path", options.SourcePath);
            writer.WriteEndObject();
            writer.WriteStartArray("breakpoints");
            writer.WriteStartObject();
            writer.WriteNumber("line", sourceLine);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
        Require(pending.GetProperty("breakpoints").GetArrayLength() == 1, "one requested source breakpoint");
        Record("dap/setBreakpoints", "session", bindingStarted);
        _ = await client.RequestAsync("configurationDone", null, cancellationToken).ConfigureAwait(false);
        _ = await client.ResponseAsync(launch, "launch", cancellationToken).ConfigureAwait(false);
        _ = await client.EventAsync("process", cancellationToken).ConfigureAwait(false);
        JsonElement stopped = await client.EventAsync("stopped", cancellationToken).ConfigureAwait(false);
        Require(stopped.GetProperty("reason").GetString() == "breakpoint", "source breakpoint stop");
        int threadId = stopped.GetProperty("threadId").GetInt32();
        Require(threadId > 0 && client.TargetProcessId > 0, "live target and thread identifiers");
        Record("dap/launch-to-breakpoint", "session", launchStarted);
        using var targetProcess = Process.GetProcessById(client.TargetProcessId);
        DateTimeOffset targetStartedAtUtc = targetProcess.StartTime.ToUniversalTime();
        await CaptureResourcesAsync("stopped-before-inspection").ConfigureAwait(false);

        await RepeatedAsync("dap/threads", async () =>
        {
            JsonElement result = await client.RequestAsync("threads", null, cancellationToken).ConfigureAwait(false);
            Require(result.GetProperty("threads").EnumerateArray().Any(item => item.GetProperty("id").GetInt32() == threadId),
                "stopped thread membership");
        }).ConfigureAwait(false);
        int frameId = 0;
        await RepeatedAsync("dap/stackTrace", async () =>
        {
            JsonElement result = await client.RequestAsync("stackTrace", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteNumber("startFrame", 0);
                writer.WriteNumber("levels", 1);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            JsonElement frames = result.GetProperty("stackFrames");
            Require(frames.GetArrayLength() == 1, "one requested stack frame");
            JsonElement frame = frames[0];
            Require(frame.GetProperty("line").GetInt32() == sourceLine &&
                frame.GetProperty("name").GetString() == "Csls.EndToEndPerformance.DebuggerPerformanceTarget.Run",
                "authored target frame and line");
            int current = frame.GetProperty("id").GetInt32();
            Require(frameId == 0 || frameId == current, "stable stopped-frame identity");
            frameId = current;
        }).ConfigureAwait(false);
        int localsReference = 0;
        await RepeatedAsync("dap/scopes", async () =>
        {
            JsonElement result = await client.RequestAsync("scopes", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("frameId", frameId);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            JsonElement locals = result.GetProperty("scopes").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "Locals");
            localsReference = locals.GetProperty("variablesReference").GetInt32();
            Require(localsReference > 0, "expandable local scope");
        }).ConfigureAwait(false);
        int numbersReference = 0;
        await RepeatedAsync("dap/variables", async () =>
        {
            JsonElement result = await client.RequestAsync("variables", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("variablesReference", localsReference);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            JsonElement variables = result.GetProperty("variables");
            JsonElement answer = variables.EnumerateArray().Single(item => item.GetProperty("name").GetString() == "answer");
            Require(answer.GetProperty("value").GetString() == "42", "live scalar local value");
            JsonElement numbers = variables.EnumerateArray().Single(item => item.GetProperty("name").GetString() == "numbers");
            numbersReference = numbers.GetProperty("variablesReference").GetInt32();
            Require(numbersReference > 0, "expandable indexed local value");
        }).ConfigureAwait(false);
        await RepeatedAsync("dap/variables/indexed-page", async () =>
        {
            JsonElement result = await client.RequestAsync("variables", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("variablesReference", numbersReference);
                writer.WriteString("filter", "indexed");
                writer.WriteNumber("start", 64);
                writer.WriteNumber("count", 32);
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            JsonElement values = result.GetProperty("variables");
            Require(values.GetArrayLength() == 32, "exact indexed page length");
            for (int index = 0; index < 32; index++)
            {
                Require(values[index].GetProperty("name").GetString() == $"[{index + 64}]" &&
                    values[index].GetProperty("value").GetString() == (index + 65).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "ordered indexed page contents");
            }
        }).ConfigureAwait(false);
        await RepeatedAsync("dap/evaluate", async () =>
        {
            JsonElement result = await client.RequestAsync("evaluate", writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("frameId", frameId);
                writer.WriteString("context", "watch");
                writer.WriteString("expression", "answer + numbers[0]");
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            Require(result.GetProperty("result").GetString() == "43", "computed watch expression");
        }).ConfigureAwait(false);
        await CaptureResourcesAsync("stopped-after-inspection").ConfigureAwait(false);

        long outputStarted = Stopwatch.GetTimestamp();
        _ = await client.RequestAsync("continue", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", threadId);
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
        _ = await client.EventAsync("terminated", cancellationToken).ConfigureAwait(false);
        int targetExit = client.TargetExitCode ?? throw new InvalidDataException("The measured target did not report its exit code.");
        Require(targetExit == 0, "successful target exit");
        Require(client.Output.Trim() == "debugger-performance-output:42:256", "independent target output");
        Record("dap/continue-output-exit", "session", outputStarted);
        long shutdownStarted = Stopwatch.GetTimestamp();
        int adapterExit = await client.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        Record("dap/shutdown", "session", shutdownStarted);
        return new DebuggerPerformanceIteration(iteration, iteration == 1 ? "first-process" : "subsequent-process",
            client.ProcessId, client.TargetProcessId, client.StartedAtUtc, targetStartedAtUtc, samples, resources, client.Output, targetExit, adapterExit);

        void Record(string name, string phase, long started) =>
            samples.Add(new DebuggerPerformanceSample(name, phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds));

        async Task RepeatedAsync(string name, Func<Task> operation)
        {
            for (int sample = 0; sample <= options.Samples; sample++)
            {
                long started = Stopwatch.GetTimestamp();
                await operation().ConfigureAwait(false);
                Record(name, sample == 0 ? "first-request" : "repeated-request", started);
            }
        }

        async Task CaptureResourcesAsync(string phase)
        {
            ProcessTreeSnapshot snapshot = await ProcessTreeReader.CaptureAsync(client.ProcessId, cancellationToken).ConfigureAwait(false);
            Require(snapshot.ProcessIds.Contains(client.TargetProcessId), "target process-tree membership");
            resources.Add(new DebuggerPerformanceResources(phase, snapshot.ProcessIds, snapshot.WorkingSetBytes,
                OperatingSystem.IsMacOS() ? null : snapshot.PrivateMemoryBytes,
                TimeSpan.FromTicks(snapshot.ProcessorTimeTicks).TotalMilliseconds));
        }
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidDataException($"The measured debugger did not preserve {description}.");
        }
    }
}
