using Csls.Debugger.Interop;
using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Performs a suspended dbgshim launch and validates ownership of the target ICorDebug instance.
/// </summary>
internal static class CorDebugRuntimeActivator
{
    /// <summary>
    /// Launches a real target suspended and verifies that its CoreCLR debugging interface activates.
    /// </summary>
    /// <param name="options">The concrete target invocation to probe.</param>
    /// <param name="cancellationToken">Cancels activation and terminates the owned target.</param>
    /// <returns>The operating-system identifier of the successfully activated probe target.</returns>
    internal static async Task<uint> VerifyAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        DbgShimLibrary.VerifyPlatformSupport();
        string commandLine = DbgShimCommandLineBuilder.Build(options);
        using var environment = DbgShimEnvironmentBlock.Create(options.Environment);
        (uint processId, nint rawResumeHandle) = CreateSuspended(
            commandLine,
            environment.Pointer,
            options.WorkingDirectory);

        if (processId == 0 || rawResumeHandle == 0)
        {
            throw new InvalidOperationException(
                "CreateProcessForLaunch succeeded without returning target ownership.");
        }

        using var resumeHandle = new DbgShimResumeHandle(rawResumeHandle);
        using var target = Process.GetProcessById(checked((int)processId));
        using var managedCallback = new CorDebugManagedCallback();
        using var registration =
            new CorDebugRuntimeStartupRegistration(processId, managedCallback);
        nint corDebug = 0;
        nint debugProcess = 0;
        try
        {
            int registerResult = DbgShimNativeMethods.RegisterForRuntimeStartup(
                processId,
                CorDebugRuntimeStartupRegistration.Callback,
                registration.Context,
                out nint unregisterToken);
            CorDebugHResult.ThrowIfFailed(registerResult, "RegisterForRuntimeStartup");
            registration.SetUnregisterToken(unregisterToken);

            int resumeResult =
                DbgShimNativeMethods.ResumeProcess(resumeHandle.DangerousGetHandle());
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
        }
        finally
        {
            await TerminateAsync(target).ConfigureAwait(false);
            if (debugProcess != 0)
            {
                _ = ComAbi.Release(debugProcess);
            }

            if (corDebug != 0)
            {
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugAbi(corDebug).Terminate(),
                    "ICorDebug.Terminate");
                _ = ComAbi.Release(corDebug);
            }
        }

        return processId;
    }

    private static unsafe (uint ProcessId, nint ResumeHandle) CreateSuspended(
        string commandLine,
        nint environment,
        string workingDirectory)
    {
        char[] mutableCommandLine = [.. commandLine, '\0'];
        uint processId;
        nint resumeHandle;
        fixed (char* commandLinePointer = mutableCommandLine)
        {
            int createResult = DbgShimNativeMethods.CreateProcessForLaunch(
                commandLinePointer,
                suspendProcess: 1,
                environment,
                workingDirectory,
                out processId,
                out resumeHandle);
            CorDebugHResult.ThrowIfFailed(createResult, "CreateProcessForLaunch");
        }

        return (processId, resumeHandle);
    }

    private static async Task TerminateAsync(Process target)
    {
        if (!target.HasExited)
        {
            target.Kill(entireProcessTree: true);
        }

        await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static void ValidateOptions(DebuggeeLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.Program) || !File.Exists(options.Program))
        {
            throw new FileNotFoundException(
                "A managed runtime activation probe requires an existing absolute program path.",
                options.Program);
        }

        if (!Path.IsPathFullyQualified(options.WorkingDirectory) ||
            !Directory.Exists(options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The debugger working directory '{options.WorkingDirectory}' does not exist.");
        }
    }
}
