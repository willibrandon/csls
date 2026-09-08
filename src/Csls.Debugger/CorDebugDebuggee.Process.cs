using Csls.Debugger.Interop;
using System.ComponentModel;
using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Owns platform-specific managed debuggee process lifecycle operations.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<int> exit = WaitForOperatingSystemExitAsync(observation.Token);
        Task<CorDebugRuntimeException> failure =
            _managedCallback.WaitForRuntimeFailureAsync(observation.Token);
        try
        {
            _ = await Task.WhenAny(exit, failure).ConfigureAwait(false);
            _managedCallback.ThrowIfRuntimeFailed();
            int exitCode = await exit.ConfigureAwait(false);
            await _managedCallback.WaitForExitProcessAsync(observation.Token).ConfigureAwait(false);
            _managedCallback.ThrowIfRuntimeFailed();
            return exitCode;
        }
        finally
        {
            await observation.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<int> WaitForOperatingSystemExitAsync(CancellationToken cancellationToken)
    {
        int exitCode;
        if (_unixExitMonitor is not null)
        {
            int? monitoredExitCode = await _unixExitMonitor.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            exitCode = monitoredExitCode ?? GetExitCode(_process);
        }
        else
        {
            await DebuggerProcessExit.WaitAsync(_process, cancellationToken).ConfigureAwait(false);
            exitCode = GetExitCode(_process);
        }

        _managedCallback.RetireProcess();
        return exitCode;
    }

    /// <inheritdoc />
    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RuntimeFailure is not null)
        {
            await TerminateProcessAsync(_process, _unixExitMonitor, _managedCallback, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool processExited = _unixExitMonitor?.IsCompleted ?? _process.HasExited;
        if (!processExited && !_managedCallback.HasCompletedExit)
        {
            TerminateManagedProcess();
        }
        _ = await WaitForOperatingSystemExitAsync(cancellationToken).ConfigureAwait(false);
    }

    private unsafe void TerminateManagedProcess()
    {
        const int ProcessTerminated = unchecked((int)0x80131301);
        var controller = new ICorDebugControllerAbi(_debugProcess);
        int running = 0;
        int* runningAddress = &running;
        int result = controller.IsRunning((nint)runningAddress);
        if (result == ProcessTerminated)
        {
            return;
        }
        CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.IsRunning");
        running = Volatile.Read(ref *runningAddress);
        if (running != 0)
        {
            result = controller.Stop(dwTimeoutIgnored: 0);
            if (result == ProcessTerminated)
            {
                return;
            }
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.Stop");
        }

        try
        {
            DebuggeeChildProcesses.Terminate(_process.Id);
        }
        finally
        {
            // CoreCLR retires queued events and marks the process as exiting before killing it.
            // It also performs the continuation needed for its terminal callback.
            result = controller.Terminate(exitCode: 0);
            if (result != ProcessTerminated)
            {
                CorDebugHResult.ThrowIfFailed(result, "ICorDebugController.Terminate");
            }
            _managedCallback.RetireProcess();
        }
    }

    private static int GetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return 0;
        }
    }

    private static async Task TerminateProcessAsync(
        Process process,
        UnixChildExitMonitor? unixExitMonitor,
        CorDebugManagedCallback? managedCallback,
        CancellationToken cancellationToken)
    {
        if (unixExitMonitor is not null)
        {
            if (!unixExitMonitor.IsCompleted)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (
                    exception is (InvalidOperationException or Win32Exception) &&
                    unixExitMonitor.IsCompleted)
                {
                    Debug.Assert(unixExitMonitor.IsCompleted);
                }
            }

            managedCallback?.RetireProcess();
            _ = await unixExitMonitor.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        managedCallback?.RetireProcess();
        await DebuggerProcessExit.WaitAsync(process, cancellationToken).ConfigureAwait(false);
    }
}
