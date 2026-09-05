using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csls.Debugger.StackProbe;

/// <summary>
/// Exercises the production control service in an isolated worker-configured process.
/// </summary>
internal static class StackProgressProbe
{
    /// <summary>
    /// Launches a recursive target, inspects its stack, and writes observed state after cleanup.
    /// </summary>
    /// <param name="root">The repository containing the real fixture source and binaries.</param>
    /// <param name="mode">The bounded request or receiver failure to exercise.</param>
    /// <param name="offset">The selected page offset.</param>
    /// <param name="checkpoint">The observed frame count at which the client cancels.</param>
    /// <param name="cancellationToken">Bounds the complete probe including target execution.</param>
    internal static async Task RunAsync(string root, string mode, int offset, int checkpoint, CancellationToken cancellationToken)
    {
        int depth = mode == "cancel" ? 100000 : 5000;
        string source = Path.Join(root, "tests", "Csls.TestProcessHost", "DebuggerDeepStackFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(source, cancellationToken).ConfigureAwait(false);
        int line = Array.FindIndex(lines, static text => text.Contains("return CompleteDescent(entered);", StringComparison.Ordinal)) + 1;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line);
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable cleanup = service.ConfigureAwait(false);
        _ = await service.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, [new(line, null)]), cancellationToken)
            .ConfigureAwait(false);
        _ = await service.LaunchAsync(new DebugLaunchRequest
        {
            Program = Path.Join(root, "artifacts", "bin", "Csls.TestProcessHost", "debug", "csls-test-process-host.dll"),
            WorkingDirectory = root,
            Arguments = ["--debugger-deep-stack-fixture", depth.ToString(CultureInfo.InvariantCulture)],
            SourceFileMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["/_/"] = root }
        }, cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot stopped = await WaitForStateAsync(service, DebugSessionState.Stopped, cancellationToken).ConfigureAwait(false);
        int threadId = stopped.StoppedThreadId ?? throw new InvalidOperationException("The target did not report its stopped thread.");
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var baseline = new StackProgressRecorder(requestCancellation, 0, "observe");
        DebugStackTrace top = await service.GetStackAsync(new DebugStackRequest(threadId, 0, 1) { Progress = baseline }, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<DebugScopeInfo> scopes = await service.GetScopesAsync(new DebugScopesRequest(top.StackFrames[0].Id), cancellationToken)
            .ConfigureAwait(false);
        DebugScopeInfo arguments = scopes.Single(static scope => scope.Name == "Arguments");
        IReadOnlyList<DebugVariableInfo> initialArguments = await ReadArgumentsAsync(service, arguments, cancellationToken).ConfigureAwait(false);
        JsonObject result = new()
        {
            ["depth"] = depth,
            ["stopped"] = JsonSerializer.SerializeToNode(stopped, StackProbeJsonContext.Default.DebugSessionSnapshot),
            ["top"] = JsonSerializer.SerializeToNode(top, StackProbeJsonContext.Default.DebugStackTrace),
            ["baseline"] = JsonSerializer.SerializeToNode(baseline.Updates[^1], StackProbeJsonContext.Default.DebugStackWalkProgress),
            ["initialArguments"] = JsonSerializer.SerializeToNode(initialArguments, StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo)
        };
        int levels = mode is "oversized" or "fail-failed" ? 0 : mode == "cancel" ? 4096 : 1000;
        var progress = new StackProgressRecorder(requestCancellation, checkpoint, mode);
        using var host = Process.GetCurrentProcess();
        host.Refresh();
        result["privateBytesBefore"] = host.PrivateMemorySize64;
        if (mode == "pre-cancel")
        {
            await requestCancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            DebugStackTrace page = await service.GetStackAsync(new DebugStackRequest(threadId, offset, levels) { Progress = progress },
                requestCancellation.Token).ConfigureAwait(false);
            result["page"] = JsonSerializer.SerializeToNode(page, StackProbeJsonContext.Default.DebugStackTrace);
        }
        catch (Exception failure) when (failure is OperationCanceledException or InvalidOperationException or AggregateException)
        {
            result["failureType"] = failure.GetType().Name;
            result["failureMessage"] = failure.Message;
            result["innerType"] = failure.InnerException?.GetType().Name;
            if (failure is AggregateException aggregate)
            {
                result["causes"] = new JsonArray([.. aggregate.Flatten().InnerExceptions.Select(static exception => JsonValue.Create(exception.GetType().Name))]);
                result["notificationCause"] = aggregate.InnerExceptions[1].InnerException?.GetType().Name;
            }
        }

        host.Refresh();
        result["privateBytesAfter"] = host.PrivateMemorySize64;
        result["updates"] = new JsonArray([.. progress.Updates.Select(static value =>
            JsonSerializer.SerializeToNode(value, StackProbeJsonContext.Default.DebugStackWalkProgress))]);
        result["afterArguments"] = JsonSerializer.SerializeToNode(await ReadArgumentsAsync(service, arguments, cancellationToken)
            .ConfigureAwait(false), StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo);
        result["unchanged"] = JsonSerializer.SerializeToNode(await service.GetSessionAsync(cancellationToken).ConfigureAwait(false),
            StackProbeJsonContext.Default.DebugSessionSnapshot);
        var recovery = new StackProgressRecorder(requestCancellation, 0, "observe");
        DebugStackTrace refreshed = await service.GetStackAsync(new DebugStackRequest(threadId, 0, 1) { Progress = recovery }, cancellationToken)
            .ConfigureAwait(false);
        result["refreshed"] = JsonSerializer.SerializeToNode(refreshed, StackProbeJsonContext.Default.DebugStackTrace);
        result["recovery"] = JsonSerializer.SerializeToNode(recovery.Updates[^1], StackProbeJsonContext.Default.DebugStackWalkProgress);
        DebugStackTrace deep = await service.GetStackAsync(new DebugStackRequest(threadId, depth - 1, 1), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DebugScopeInfo> deepScopes = await service.GetScopesAsync(new DebugScopesRequest(deep.StackFrames[0].Id), cancellationToken)
            .ConfigureAwait(false);
        result["deepArguments"] = JsonSerializer.SerializeToNode(await ReadArgumentsAsync(service,
            deepScopes.Single(static scope => scope.Name == "Arguments"), cancellationToken).ConfigureAwait(false),
            StackProbeJsonContext.Default.IReadOnlyListDebugVariableInfo);
        var tailProgress = new StackProgressRecorder(requestCancellation, 0, "observe");
        DebugStackTrace tail = await service.GetStackAsync(new DebugStackRequest(threadId, depth, 64) { Progress = tailProgress }, cancellationToken)
            .ConfigureAwait(false);
        result["tail"] = JsonSerializer.SerializeToNode(tail, StackProbeJsonContext.Default.DebugStackTrace);
        result["tailProgress"] = JsonSerializer.SerializeToNode(tailProgress.Updates[^1], StackProbeJsonContext.Default.DebugStackWalkProgress);
        var emptyProgress = new StackProgressRecorder(requestCancellation, 0, "observe");
        DebugStackTrace empty = await service.GetStackAsync(new DebugStackRequest(threadId, depth + 100, 1) { Progress = emptyProgress }, cancellationToken)
            .ConfigureAwait(false);
        result["empty"] = JsonSerializer.SerializeToNode(empty, StackProbeJsonContext.Default.DebugStackTrace);
        result["emptyProgress"] = JsonSerializer.SerializeToNode(emptyProgress.Updates[^1], StackProbeJsonContext.Default.DebugStackWalkProgress);
        _ = await service.SetSourceBreakpointsAsync(new DebugSourceBreakpointSetRequest(source, []), cancellationToken).ConfigureAwait(false);
        _ = await service.ContinueAsync(cancellationToken).ConfigureAwait(false);
        DebugSessionSnapshot terminated = await WaitForStateAsync(service, DebugSessionState.Terminated, cancellationToken).ConfigureAwait(false);
        result["exitCode"] = terminated.ExitCode;
        DebugOutputPage output = await service.GetOutputAsync(new DebugOutputRequest(0, 256), cancellationToken).ConfigureAwait(false);
        result["output"] = string.Concat(output.Entries.Select(static value => value.Output));
        await Console.Out.WriteLineAsync(result.ToJsonString()).ConfigureAwait(false);
    }

    private static Task<IReadOnlyList<DebugVariableInfo>> ReadArgumentsAsync(DebuggerControlService service,
        DebugScopeInfo scope, CancellationToken cancellationToken) => service.GetVariablesAsync(
            new DebugVariablesRequest(scope.VariablesReference, 0, 0, AllowTargetCodeExecution: false), cancellationToken);

    private static async Task<DebugSessionSnapshot> WaitForStateAsync(DebuggerControlService service, DebugSessionState state,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            DebugSessionSnapshot snapshot = await service.GetSessionAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.State == state)
            {
                return snapshot;
            }

            if (snapshot.State is DebugSessionState.Faulted or DebugSessionState.Terminated)
            {
                throw new InvalidOperationException($"Target reached {snapshot.State} instead of {state}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }
}
