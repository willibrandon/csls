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
    /// <param name="observer">Receives debugger diagnostics through the session output channel.</param>
    /// <param name="sourceBreakpoints">The session source-breakpoint owner.</param>
    /// <param name="functionBreakpoints">The session function-breakpoint owner.</param>
    /// <param name="instructionBreakpoints">The session managed-IL breakpoint owner.</param>
    /// <param name="breakpointReached">The ordered runtime-breakpoint decision callback.</param>
    /// <param name="targetBreakpointReached">The ordered targeted-step breakpoint callback.</param>
    /// <param name="stepCompleted">The ordered runtime-step completion callback.</param>
    /// <param name="exceptionRaised">The ordered managed-exception callback.</param>
    /// <param name="evaluationCompleted">The ordered function-evaluation completion callback.</param>
    /// <param name="cancellationToken">Cancels runtime activation and cleans up the target.</param>
    /// <returns>The live debugger-owned target.</returns>
    internal static async Task<CorDebugDebuggee> LaunchAsync(
        DebuggeeLaunchOptions options,
        DebuggerSessionActor actor,
        IDebuggerSessionObserver observer,
        SourceBreakpointManager sourceBreakpoints,
        FunctionBreakpointManager functionBreakpoints,
        InstructionBreakpointManager instructionBreakpoints,
        Func<int, ManagedBreakpointHit, CancellationToken, ValueTask<bool>> breakpointReached,
        Func<int, nint, CancellationToken, ValueTask<ManagedTargetBreakpointDecision>>
            targetBreakpointReached,
        Func<int, nint, int, CancellationToken, ValueTask<bool>> stepCompleted,
        Func<int, nint, DebugExceptionStage, CancellationToken, ValueTask<bool>> exceptionRaised,
        Func<nint, bool, CancellationToken, ValueTask<bool>> evaluationCompleted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(sourceBreakpoints);
        ArgumentNullException.ThrowIfNull(functionBreakpoints);
        ArgumentNullException.ThrowIfNull(instructionBreakpoints);
        ArgumentNullException.ThrowIfNull(breakpointReached);
        ArgumentNullException.ThrowIfNull(targetBreakpointReached);
        ArgumentNullException.ThrowIfNull(stepCompleted);
        ArgumentNullException.ThrowIfNull(exceptionRaised);
        ArgumentNullException.ThrowIfNull(evaluationCompleted);
        ValidateOptions(options);
        DbgShimLibrary.VerifyPlatformSupport();

        string commandLine = DbgShimCommandLineBuilder.Build(options);
        using var environment = DbgShimEnvironmentBlock.Create(options.Environment);
        var standardStreamsOwner = new DbgShimStandardStreamsOwner();
        await using ConfiguredAsyncDisposable standardStreamsOwnerScope =
            standardStreamsOwner.ConfigureAwait(false);
        DbgShimStandardStreams standardStreams = standardStreamsOwner.Value;
        using var processOwner = new DisposableOwner<Process>();
        using var managedCallbackOwner = new DisposableOwner<CorDebugManagedCallback>();
        using var registrationOwner =
            new DisposableOwner<CorDebugRuntimeStartupRegistration>();
        UnixChildExitMonitor? unixExitMonitor = null;
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
            processOwner.Acquire(() => Process.GetProcessById(checked((int)processId)));
            _ = processOwner.Value
                ?? throw new InvalidOperationException("The debuggee process was not acquired.");
            if (!OperatingSystem.IsWindows())
            {
                unixExitMonitor = UnixChildExitMonitor.Start(processId);
            }

            managedCallbackOwner.Acquire(() =>
                new CorDebugManagedCallback(
                actor,
                observer,
                sourceBreakpoints,
                functionBreakpoints,
                instructionBreakpoints,
                breakpointReached,
                targetBreakpointReached,
                stepCompleted,
                exceptionRaised,
                evaluationCompleted));
            CorDebugManagedCallback managedCallback = managedCallbackOwner.Value
                ?? throw new InvalidOperationException("The managed callback was not created.");
            registrationOwner.Acquire(() =>
                new CorDebugRuntimeStartupRegistration(
                    processId,
                    actor,
                    managedCallback,
                    sourceBreakpoints));
            CorDebugRuntimeStartupRegistration registration = registrationOwner.Value
                ?? throw new InvalidOperationException(
                    "The runtime-startup registration was not created.");
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
            managedCallback.ThrowIfRuntimeFailed();

            var result = new CorDebugDebuggee(
                actor,
                sourceBreakpoints,
                functionBreakpoints,
                instructionBreakpoints,
                managedCallbackOwner,
                registrationOwner,
                standardStreamsOwner,
                processOwner,
                unixExitMonitor,
                ownsProcess: true,
                ownsRuntimeLease: true,
                activation);
            ownsActivationGate = false;
            unixExitMonitor = null;
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

            if (processOwner.Value is Process process)
            {
                await TerminateProcessAsync(process, unixExitMonitor, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (corDebug != 0 && managedCallbackOwner.Value is CorDebugManagedCallback callback)
            {
                await callback.WaitForExitProcessAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (corDebug != 0 || debugProcess != 0)
            {
                await ReleaseRuntimeAsync(
                    actor,
                    corDebug,
                    debugProcess,
                    managedCallbackOwner.Value)
                    .ConfigureAwait(false);
            }
        }
    }

}
