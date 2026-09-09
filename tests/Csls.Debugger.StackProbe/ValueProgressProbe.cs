using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csls.Debugger.StackProbe;

/// <summary>
/// Exercises live value cancellation and ownership in an isolated worker-configured process.
/// </summary>
internal static class ValueProgressProbe
{
    /// <summary>
    /// Inspects real expandable arrays and reports cancellation, registry ownership, and recovery.
    /// </summary>
    /// <param name="root">The repository containing the checked-in fixture and binaries.</param>
    /// <param name="mode">The request or client notification failure to exercise.</param>
    /// <param name="checkpoint">The formatted-value count at which the client cancels.</param>
    /// <param name="cancellationToken">Bounds the complete probe and its owned target.</param>
    internal static async Task RunAsync(string root, string mode, int checkpoint, CancellationToken cancellationToken)
    {
        string source = Path.Join(root, "tests", "Csls.TestProcessHost", "DebuggerDumpArrayFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(source, cancellationToken).ConfigureAwait(false);
        int line = Array.FindIndex(lines, static text => text.Contains("DebuggerBlockingWait.Wait(announcement);", StringComparison.Ordinal)) + 1;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line);
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable cleanup = service.ConfigureAwait(false);
        _ = await service.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, [new(line, null)]), cancellationToken)
            .ConfigureAwait(false);
        _ = await service.LaunchAsync(new DebugLaunchRequest
        {
            Program = Path.Join(root, "artifacts", "bin", "Csls.TestProcessHost", "debug", "csls-test-process-host.dll"),
            WorkingDirectory = root,
            Arguments = ["--debugger-dump-arrays", nameof(ValueProgressProbe)],
            SourceFileMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["/_/"] = root }
        }, cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStopAsync(service, cancellationToken).ConfigureAwait(false);
        int thread = stopped.StoppedThreadId ?? throw new InvalidOperationException("The target did not report a stopped thread.");
        DebugStackTrace stack = await service.GetStackAsync(new DebugStackRequest(thread, 0, 1), cancellationToken).ConfigureAwait(false);
        int frame = stack.StackFrames[0].Id;
        DebugEvaluateResult array = await service.EvaluateAsync(new DebugEvaluateRequest(frame, "many"), cancellationToken).ConfigureAwait(false);
        int reference = array.VariablesReference;
        IReadOnlyList<DebugVariableInfo> published = await ReadPageAsync(service, reference, 0, 1, cancellationToken).ConfigureAwait(false);
        int child = published.Single().VariablesReference;
        IReadOnlyList<DebugVariableInfo> initial = await ReadPageAsync(service, child, 0, 1, cancellationToken).ConfigureAwait(false);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var baseline = new ValueProgressRecorder(requestCancellation, 0, "observe");
        _ = await ReadPageAsync(service, reference, 65537, 1, cancellationToken, baseline).ConfigureAwait(false);
        JsonObject result = new()
        {
            ["reference"] = reference,
            ["stopped"] = JsonSerializer.SerializeToNode(stopped, StackProbeJsonContext.Default.DebugSessionSnapshot),
            ["stack"] = JsonSerializer.SerializeToNode(stack, StackProbeJsonContext.Default.DebugStackTrace),
            ["initial"] = JsonSerializer.SerializeToNode(initial, StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo),
            ["baseline"] = JsonSerializer.SerializeToNode(baseline.Updates.Single(), StackProbeJsonContext.Default.DebugValueReadProgress)
        };
        if (mode == "pre-cancel")
        {
            await requestCancellation.CancelAsync().ConfigureAwait(false);
        }

        var progress = new ValueProgressRecorder(requestCancellation, checkpoint, mode);
        try
        {
            IReadOnlyList<DebugVariableInfo> page = await ReadPageAsync(service, reference, mode == "empty" ? 65537 : 0,
                mode is "oversized" or "fail-failed" ? 65537 : 1000, requestCancellation.Token, progress).ConfigureAwait(false);
            result["page"] = JsonSerializer.SerializeToNode(page, StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo);
        }
        catch (Exception failure) when (failure is OperationCanceledException or InvalidOperationException or AggregateException or FormatException)
        {
            result["failureType"] = failure.GetType().Name;
            result["failureMessage"] = failure.Message;
            result["innerType"] = failure.InnerException?.GetType().Name;
            result["cancellationMatches"] = failure is OperationCanceledException canceled && canceled.CancellationToken == requestCancellation.Token;
            if (failure is AggregateException aggregate)
            {
                result["causes"] = new JsonArray([.. aggregate.InnerExceptions.Select(static value => JsonValue.Create(value.GetType().Name))]);
                result["notificationCause"] = aggregate.InnerExceptions[1].InnerException?.GetType().Name;
            }
        }

        result["updates"] = new JsonArray([.. progress.Updates.Select(static value =>
            JsonSerializer.SerializeToNode(value, StackProbeJsonContext.Default.DebugValueReadProgress))]);
        var recovery = new ValueProgressRecorder(requestCancellation, 0, "observe");
        _ = await ReadPageAsync(service, reference, 65537, 1, cancellationToken, recovery).ConfigureAwait(false);
        result["recovery"] = JsonSerializer.SerializeToNode(recovery.Updates.Single(), StackProbeJsonContext.Default.DebugValueReadProgress);
        result["originalChild"] = JsonSerializer.SerializeToNode(await ReadPageAsync(service, child, 0, 1, cancellationToken)
            .ConfigureAwait(false), StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo);
        IReadOnlyList<DebugVariableInfo> tail = await ReadPageAsync(service, reference, 65536, 1, cancellationToken).ConfigureAwait(false);
        result["tail"] = JsonSerializer.SerializeToNode(tail, StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo);
        result["tailChild"] = JsonSerializer.SerializeToNode(await ReadPageAsync(service, tail.Single().VariablesReference, 0, 1, cancellationToken)
            .ConfigureAwait(false), StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo);
        result["unchanged"] = JsonSerializer.SerializeToNode(await service.GetSessionAsync(cancellationToken).ConfigureAwait(false),
            StackProbeJsonContext.Default.DebugSessionSnapshot);
        result["refreshed"] = JsonSerializer.SerializeToNode(await service.GetStackAsync(new DebugStackRequest(thread, 0, 1), cancellationToken)
            .ConfigureAwait(false), StackProbeJsonContext.Default.DebugStackTrace);
        result["terminated"] = JsonSerializer.SerializeToNode(await service.TerminateAsync(cancellationToken).ConfigureAwait(false),
            StackProbeJsonContext.Default.DebugSessionSnapshot);
        await Console.Out.WriteLineAsync(result.ToJsonString()).ConfigureAwait(false);
    }

    private static Task<IReadOnlyList<DebugVariableInfo>> ReadPageAsync(DebuggerControlService service, int reference,
        int start, int count, CancellationToken cancellationToken, IProgress<DebugValueReadProgress>? progress = null) =>
        service.GetVariablesAsync(new DebugVariablesRequest(reference, start, count, AllowTargetCodeExecution: false,
            DebugVariableFilter.Indexed)
        { Progress = progress }, cancellationToken);

    private static async Task<DebugSessionSnapshot> WaitForStopAsync(DebuggerControlService service, CancellationToken cancellationToken)
    {
        while (true)
        {
            DebugSessionSnapshot snapshot = await service.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.State == DebugSessionState.Stopped)
            {
                return snapshot;
            }
            if (snapshot.State is DebugSessionState.Faulted or DebugSessionState.Terminated)
            {
                throw new InvalidOperationException($"The target reached {snapshot.State} before its source breakpoint.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }
}
