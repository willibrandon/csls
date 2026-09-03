using Csls.Debugger.Interop;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Debugger;

/// <summary>
/// Activates launched and attached CoreCLR debugger targets.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <summary>
    /// Launches a target suspended and activates its CoreCLR debugging interface.
    /// </summary>
    /// <param name="options">The validated target invocation.</param>
    /// <param name="actor">The session actor that owns runtime calls and callbacks.</param>
    /// <param name="sourceBreakpoints">The session source-breakpoint owner.</param>
    /// <param name="breakpointStopped">The ordered runtime-breakpoint stop callback.</param>
    /// <param name="stepCompleted">The ordered runtime-step completion callback.</param>
    /// <param name="exceptionRaised">The ordered managed-exception callback.</param>
    /// <param name="cancellationToken">Cancels runtime activation and cleans up the target.</param>
    /// <returns>The live debugger-owned target.</returns>
    internal static async Task<CorDebugDebuggee> LaunchAsync(
        DebuggeeLaunchOptions options,
        DebuggerSessionActor actor,
        SourceBreakpointManager sourceBreakpoints,
        Func<int, CancellationToken, ValueTask> breakpointStopped,
        Func<int, nint, int, CancellationToken, ValueTask<bool>> stepCompleted,
        Func<int, nint, DebugExceptionStage, CancellationToken, ValueTask<bool>> exceptionRaised,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(sourceBreakpoints);
        ArgumentNullException.ThrowIfNull(breakpointStopped);
        ArgumentNullException.ThrowIfNull(stepCompleted);
        ArgumentNullException.ThrowIfNull(exceptionRaised);
        ValidateOptions(options);
        DbgShimLibrary.VerifyPlatformSupport();

        string commandLine = DbgShimCommandLineBuilder.Build(options);
        using var environment = DbgShimEnvironmentBlock.Create(options.Environment);
        var standardStreamsOwner = new DbgShimStandardStreamsOwner();
        await using ConfiguredAsyncDisposable standardStreamsOwnerScope =
            standardStreamsOwner.ConfigureAwait(false);
        DbgShimStandardStreams standardStreams = standardStreamsOwner.Value;
        Process? process = null;
        CorDebugManagedCallback? managedCallback = null;
        CorDebugRuntimeStartupRegistration? registration = null;
        Task<int>? unixExitCode = null;
        nint corDebug = 0;
        nint debugProcess = 0;
        bool ownsActivationGate = false;
        try
        {
            await CorDebugRuntimeActivationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            ownsActivationGate = true;
            (uint processId, nint rawResumeHandle) = await standardStreams.CreateSuspendedAsync(
                commandLine,
                environment.Pointer,
                options.WorkingDirectory,
                cancellationToken).ConfigureAwait(false);
            if (processId == 0 || rawResumeHandle == 0)
            {
                throw new InvalidOperationException(
                    "CreateProcessForLaunch succeeded without returning target ownership.");
            }

            using var resumeHandle = new DbgShimResumeHandle(rawResumeHandle);
            process = Process.GetProcessById(checked((int)processId));
            if (!OperatingSystem.IsWindows())
            {
                unixExitCode = UnixChildExitMonitor.StartAsync(processId);
            }

            managedCallback = new CorDebugManagedCallback(
                actor,
                sourceBreakpoints,
                breakpointStopped,
                stepCompleted,
                exceptionRaised);
            registration = new CorDebugRuntimeStartupRegistration(
                processId,
                actor,
                managedCallback);
            int registerResult = DbgShimNativeMethods.RegisterForRuntimeStartup(
                processId,
                CorDebugRuntimeStartupRegistration.Callback,
                registration.Context,
                out nint unregisterToken);
            CorDebugHResult.ThrowIfFailed(registerResult, "RegisterForRuntimeStartup");
            registration.SetUnregisterToken(unregisterToken);

            int resumeResult = DbgShimNativeMethods.ResumeProcess(
                resumeHandle.DangerousGetHandle());
            CorDebugHResult.ThrowIfFailed(resumeResult, "ResumeProcess");
            CorDebugHResult.ThrowIfFailed(
                DbgShimNativeMethods.CloseResumeHandle(rawResumeHandle),
                "CloseResumeHandle");
            resumeHandle.SetHandleAsInvalid();

            CorDebugActivationResult activation =
                await registration.WaitAsync(cancellationToken).ConfigureAwait(false);
            corDebug = activation.CorDebug;
            debugProcess = activation.Process;
            await managedCallback.WaitForCreateProcessAsync(cancellationToken)
                .ConfigureAwait(false);
            CorDebugRuntimeActivationGate.Release();
            ownsActivationGate = false;

            var result = new CorDebugDebuggee(
                actor,
                managedCallback,
                registration,
                standardStreamsOwner.Detach(),
                process,
                unixExitCode,
                ownsProcess: true,
                activation);
            managedCallback = null;
            registration = null;
            process = null;
            unixExitCode = null;
            corDebug = 0;
            debugProcess = 0;
            return result;
        }
        finally
        {
            if (ownsActivationGate)
            {
                CorDebugRuntimeActivationGate.Release();
            }

            if (process is not null)
            {
                await TerminateProcessAsync(process, CancellationToken.None).ConfigureAwait(false);
                process.Dispose();
            }

            if (unixExitCode is not null)
            {
                _ = await unixExitCode.ConfigureAwait(false);
            }

            if (corDebug != 0 && managedCallback is not null)
            {
                await managedCallback.WaitForExitProcessAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (corDebug != 0 || debugProcess != 0)
            {
                await ReleaseRuntimeAsync(actor, corDebug, debugProcess).ConfigureAwait(false);
            }

            registration?.Dispose();
            managedCallback?.Dispose();
        }
    }

    /// <summary>
    /// Attaches to an existing process after its CoreCLR runtime has started.
    /// </summary>
    /// <param name="processId">The operating-system process identifier.</param>
    /// <param name="actor">The session actor that owns runtime calls and callbacks.</param>
    /// <param name="sourceBreakpoints">The session source-breakpoint owner.</param>
    /// <param name="breakpointStopped">The ordered runtime-breakpoint stop callback.</param>
    /// <param name="stepCompleted">The ordered runtime-step completion callback.</param>
    /// <param name="exceptionRaised">The ordered managed-exception callback.</param>
    /// <param name="cancellationToken">Cancels runtime activation without terminating the target.</param>
    /// <returns>The live debugger attachment.</returns>
    internal static async Task<CorDebugDebuggee> AttachAsync(
        int processId,
        DebuggerSessionActor actor,
        SourceBreakpointManager sourceBreakpoints,
        Func<int, CancellationToken, ValueTask> breakpointStopped,
        Func<int, nint, int, CancellationToken, ValueTask<bool>> stepCompleted,
        Func<int, nint, DebugExceptionStage, CancellationToken, ValueTask<bool>> exceptionRaised,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(sourceBreakpoints);
        ArgumentNullException.ThrowIfNull(breakpointStopped);
        ArgumentNullException.ThrowIfNull(stepCompleted);
        ArgumentNullException.ThrowIfNull(exceptionRaised);
        if (processId == Environment.ProcessId)
        {
            throw new ArgumentException("The debugger cannot attach to its own process.", nameof(processId));
        }

        DbgShimLibrary.VerifyPlatformSupport();
        Process? process = null;
        CorDebugManagedCallback? managedCallback = null;
        CorDebugRuntimeStartupRegistration? registration = null;
        nint corDebug = 0;
        nint debugProcess = 0;
        bool ownsActivationGate = false;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Process {processId} has already exited.");
            }

            await CorDebugRuntimeActivationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            ownsActivationGate = true;
            _ = DbgShimRuntimeDiscovery.GetSingleRuntimePath(checked((uint)processId));
            managedCallback = new CorDebugManagedCallback(
                actor,
                sourceBreakpoints,
                breakpointStopped,
                stepCompleted,
                exceptionRaised);
            registration = new CorDebugRuntimeStartupRegistration(
                checked((uint)processId),
                actor,
                managedCallback);
            CorDebugHResult.ThrowIfFailed(
                DbgShimNativeMethods.RegisterForRuntimeStartup(
                    checked((uint)processId),
                    CorDebugRuntimeStartupRegistration.Callback,
                    registration.Context,
                    out nint unregisterToken),
                "RegisterForRuntimeStartup");
            registration.SetUnregisterToken(unregisterToken);

            CorDebugActivationResult activation = await registration
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            corDebug = activation.CorDebug;
            debugProcess = activation.Process;
            await managedCallback.WaitForCreateProcessAsync(cancellationToken)
                .ConfigureAwait(false);
            CorDebugRuntimeActivationGate.Release();
            ownsActivationGate = false;

            var result = new CorDebugDebuggee(
                actor,
                managedCallback,
                registration,
                standardStreams: null,
                process,
                unixExitCode: null,
                ownsProcess: false,
                activation);
            managedCallback = null;
            registration = null;
            process = null;
            corDebug = 0;
            debugProcess = 0;
            return result;
        }
        finally
        {
            if (ownsActivationGate)
            {
                CorDebugRuntimeActivationGate.Release();
            }

            if (corDebug != 0 || debugProcess != 0)
            {
                await DetachRuntimeAsync(actor, corDebug, debugProcess).ConfigureAwait(false);
            }

            registration?.Dispose();
            managedCallback?.Dispose();
            process?.Dispose();
        }
    }

}
