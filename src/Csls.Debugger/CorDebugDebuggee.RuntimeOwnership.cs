using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Releases or detaches debugger runtime COM ownership on the engine actor.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private static async Task ReleaseRuntimeAsync(
        DebuggerSessionActor actor,
        nint corDebug,
        nint debugProcess,
        CorDebugManagedCallback? managedCallback)
    {
        if (corDebug == 0 && debugProcess == 0)
        {
            return;
        }

        await actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                ReleaseRuntimeReferences(
                    corDebug,
                    debugProcess,
                    runtimeAvailable: managedCallback?.RuntimeFailure is null);

                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DetachRuntimeAsync(
        DebuggerSessionActor actor,
        nint corDebug,
        nint debugProcess,
        CorDebugManagedCallback? managedCallback)
    {
        if (corDebug == 0 && debugProcess == 0)
        {
            return;
        }

        await actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                DetachRuntimeReferences(corDebug, debugProcess, managedCallback);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static void ReleaseRuntimeReferences(
        nint corDebug,
        nint debugProcess,
        bool runtimeAvailable = true)
    {
        if (debugProcess != 0)
        {
            _ = ComAbi.Release(debugProcess);
        }

        if (corDebug != 0)
        {
            if (runtimeAvailable)
            {
                _ = new ICorDebugAbi(corDebug).Terminate();
            }

            _ = ComAbi.Release(corDebug);
        }
    }

    private static void DetachRuntimeReferences(
        nint corDebug,
        nint debugProcess,
        CorDebugManagedCallback? managedCallback)
    {
        try
        {
            if (debugProcess != 0 && managedCallback?.RuntimeFailure is null)
            {
                int result = new ICorDebugControllerAbi(debugProcess).Detach();
                managedCallback?.ThrowIfRuntimeFailed();
                CorDebugHResult.ThrowIfFailed(
                    result,
                    "ICorDebugController.Detach");
            }
        }
        finally
        {
            ReleaseRuntimeReferences(
                corDebug,
                debugProcess,
                runtimeAvailable: managedCallback?.RuntimeFailure is null);
        }
    }

    private static void ValidateOptions(DebuggeeLaunchOptions options)
    {
        if (!Path.IsPathFullyQualified(options.Program) || !File.Exists(options.Program))
        {
            throw new FileNotFoundException(
                "A managed launch requires an existing absolute program path.",
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
