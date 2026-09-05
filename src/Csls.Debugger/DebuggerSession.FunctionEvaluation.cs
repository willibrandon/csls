using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Coordinates bounded target-code evaluation outside the CoreCLR callback actor.
/// </summary>
public sealed partial class DebuggerSession
{
    private static readonly TimeSpan s_functionEvaluationDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_functionEvaluationAbortGrace = TimeSpan.FromSeconds(5);

    private async Task<ManagedFunctionEvaluationResult> WaitForFunctionEvaluationAsync(
        CorDebugDebuggee debuggee,
        Task<ManagedFunctionEvaluationResult> completion,
        CancellationToken cancellationToken)
    {
        CancellationToken sessionCancellation = _lifetime.Token;
        try
        {
            return await WaitForFunctionEvaluationCoreAsync(debuggee, completion, cancellationToken)
                .WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await SynchronizeFunctionEvaluationCompletionAsync(sessionCancellation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for callback-owned state publication before exposing evaluation completion to callers.
    /// </summary>
    private async Task SynchronizeFunctionEvaluationCompletionAsync(CancellationToken sessionCancellation)
    {
        try
        {
            await _actor.InvokeAsync(
                static _ => ValueTask.CompletedTask,
                sessionCancellation).WaitAsync(sessionCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            System.Diagnostics.Debug.Assert(Volatile.Read(ref _disposed) != 0);
        }
        catch (Exception exception) when (
            Volatile.Read(ref _disposed) != 0 &&
            exception is ObjectDisposedException or System.Threading.Channels.ChannelClosedException)
        {
            System.Diagnostics.Debug.Assert(sessionCancellation.IsCancellationRequested);
        }
    }

    private async Task<ManagedFunctionEvaluationResult> WaitForFunctionEvaluationCoreAsync(
        CorDebugDebuggee debuggee,
        Task<ManagedFunctionEvaluationResult> completion,
        CancellationToken cancellationToken)
    {
        bool canceled = false;
        bool timedOut = false;
        try
        {
            return await completion.WaitAsync(
                s_functionEvaluationDeadline,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        await AbortAndSettleFunctionEvaluationAsync(debuggee, completion)
            .WaitAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (canceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (timedOut)
        {
            throw new TimeoutException(
                "Managed function evaluation exceeded its five-second deadline and was " +
                "canceled cooperatively.");
        }

        throw new InvalidOperationException(
            "Managed function evaluation ended without a result or failure.");
    }

    private async Task AbortAndSettleFunctionEvaluationAsync(
        CorDebugDebuggee debuggee,
        Task<ManagedFunctionEvaluationResult> completion)
    {
        Exception? abortFailure = null;
        try
        {
            await _actor.InvokeAsync(
                token =>
                {
                    _ = token;
                    _ = debuggee.AbortFunctionEvaluation();
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            abortFailure = exception;
        }

        try
        {
            _ = await completion.WaitAsync(
                s_functionEvaluationAbortGrace,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            completion.IsCompleted &&
            exception is OperationCanceledException or InvalidOperationException)
        {
            System.Diagnostics.Debug.Assert(completion.IsCompleted);
        }
        catch (TimeoutException)
        {
            string reason = abortFailure is null
                ? "Managed function evaluation did not settle after cooperative Abort."
                : $"Managed function evaluation rejected cooperative Abort: " +
                    abortFailure.Message;
            reason += " The debugger will not invoke RudeAbort because it can corrupt target " +
                "state. This debugger session is faulted and must be disconnected.";
            await _actor.InvokeAsync(
                token =>
                {
                    _ = token;
                    debuggee.DisableFunctionEvaluation(reason);
                    _stopGeneration = _stopGeneration.Next();
                    _state = DebugSessionState.Faulted;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(reason);
        }
    }

    private ValueTask<bool> HandleRuntimeEvaluationCoreAsync(
        nint evaluation,
        bool isException,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        DebugStopGeneration resultGeneration = _stopGeneration.Next();
        if (_debuggee is not CorDebugDebuggee managedDebuggee)
        {
            return ValueTask.FromResult(false);
        }

        bool recognized = managedDebuggee.CompleteFunctionEvaluation(
            evaluation,
            isException,
            resultGeneration);
        if (recognized)
        {
            _stopGeneration = resultGeneration;
            _state = (managedDebuggee.FunctionEvaluationSafetyFailure,
                managedDebuggee.IsFunctionEvaluationActive) switch
            {
                (not null, _) => DebugSessionState.Faulted,
                (_, true) => DebugSessionState.Running,
                _ => DebugSessionState.Stopped
            };
        }

        return ValueTask.FromResult(recognized);
    }
}
