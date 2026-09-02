using Csls.Debugger.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Owns one dbgshim runtime-startup callback registration and its native callback context.
/// </summary>
internal sealed class CorDebugRuntimeStartupRegistration : IDisposable
{
    private readonly TaskCompletionSource<CorDebugActivationResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly uint _processId;
    private readonly DebuggerSessionActor _actor;
    private readonly CorDebugManagedCallback _managedCallback;
    private GCHandle _context;
    private DbgShimRegistrationHandle? _unregisterHandle;
    private int _disposed;

    /// <summary>
    /// Creates a rooted callback context for one runtime-startup registration.
    /// </summary>
    /// <param name="processId">The operating-system target identifier to attach.</param>
    /// <param name="actor">The engine actor that owns runtime activation.</param>
    /// <param name="managedCallback">The callback object installed before attach.</param>
    internal CorDebugRuntimeStartupRegistration(
        uint processId,
        DebuggerSessionActor actor,
        CorDebugManagedCallback managedCallback)
    {
        ArgumentOutOfRangeException.ThrowIfZero(processId);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(managedCallback);
        _processId = processId;
        _actor = actor;
        _managedCallback = managedCallback;
        _context = GCHandle.Alloc(this, GCHandleType.Normal);
    }

    /// <summary>
    /// Gets the unmanaged callback function passed to dbgshim.
    /// </summary>
    internal static unsafe nint Callback =>
        (nint)(delegate* unmanaged[Stdcall]<nint, nint, int, void>)&OnRuntimeStartup;

    /// <summary>
    /// Gets the opaque callback context passed to dbgshim.
    /// </summary>
    internal nint Context => GCHandle.ToIntPtr(_context);

    /// <summary>
    /// Records the token returned by a successful dbgshim registration.
    /// </summary>
    /// <param name="unregisterToken">The non-null dbgshim registration token.</param>
    internal void SetUnregisterToken(nint unregisterToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(unregisterToken);
        var handle = new DbgShimRegistrationHandle(unregisterToken);
        if (Interlocked.CompareExchange(ref _unregisterHandle, handle, null) is not null)
        {
            handle.Dispose();
            throw new InvalidOperationException(
                "A runtime-startup registration token was already recorded.");
        }
    }

    /// <summary>
    /// Waits for dbgshim to report runtime startup or activation failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting without discarding native ownership.</param>
    /// <returns>The transferred ICorDebug and ICorDebugProcess interface pointers.</returns>
    internal Task<CorDebugActivationResult> WaitAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _unregisterHandle, null)?.Dispose();

        if (_context.IsAllocated)
        {
            _context.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnRuntimeStartup(nint corDebug, nint parameter, int hresult)
    {
        if (parameter == 0)
        {
            return;
        }

        var context = GCHandle.FromIntPtr(parameter);
        if (context.Target is CorDebugRuntimeStartupRegistration registration)
        {
            registration.QueueRuntimeStartup(corDebug, hresult);
        }
    }

    private void QueueRuntimeStartup(nint callbackObject, int callbackResult)
    {
        nint ownedCallbackObject = callbackObject;
        Task operation = _actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                nint currentCallbackObject = Interlocked.Exchange(
                    ref ownedCallbackObject,
                    0);
                CompleteRuntimeStartup(currentCallbackObject, callbackResult);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        _ = ObserveRuntimeStartupAsync(
            operation,
            () =>
            {
                nint currentCallbackObject = Interlocked.Exchange(
                    ref ownedCallbackObject,
                    0);
                if (currentCallbackObject != 0)
                {
                    _ = ComAbi.Release(currentCallbackObject);
                }
            });
    }

    private async Task ObserveRuntimeStartupAsync(Task operation, Action releaseUnclaimed)
    {
        try
        {
            await operation.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException or
                OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        {
            releaseUnclaimed();
            _ = _completion.TrySetException(exception);
        }
    }

    private unsafe void CompleteRuntimeStartup(nint callbackObject, int callbackResult)
    {
        CorDebugHResult.ThrowIfFailed(callbackResult, "Runtime startup");
        if (callbackObject == 0)
        {
            throw new InvalidOperationException(
                "Runtime startup succeeded without returning an ICorDebug object.");
        }

        nint corDebug = 0;
        nint attachedProcess = 0;
        try
        {
            corDebug = ComAbi.QueryInterface(callbackObject, ICorDebugAbi.InterfaceId);
            _ = ComAbi.Release(callbackObject);
            callbackObject = 0;

            var api = new ICorDebugAbi(corDebug);
            CorDebugHResult.ThrowIfFailed(api.Initialize(), "ICorDebug.Initialize");
            CorDebugHResult.ThrowIfFailed(
                api.SetManagedHandler(_managedCallback.Pointer),
                "ICorDebug.SetManagedHandler");
            nint nativeProcess = 0;
            nint* processAddress = &nativeProcess;
            CorDebugHResult.ThrowIfFailed(
                api.DebugActiveProcess(_processId, win32Attach: 0, (nint)processAddress),
                "ICorDebug.DebugActiveProcess");
            attachedProcess = Volatile.Read(ref *processAddress);
            if (attachedProcess == 0)
            {
                throw new InvalidOperationException(
                    "ICorDebug.DebugActiveProcess succeeded without returning a process.");
            }

            var result = new CorDebugActivationResult(corDebug, attachedProcess);
            if (!_completion.TrySetResult(result))
            {
                throw new InvalidOperationException(
                    "The runtime-startup callback completed more than once.");
            }

            corDebug = 0;
            attachedProcess = 0;
        }
        finally
        {
            if (attachedProcess != 0)
            {
                _ = ComAbi.Release(attachedProcess);
            }

            if (corDebug != 0)
            {
                _ = ComAbi.Release(corDebug);
            }

            if (callbackObject != 0)
            {
                _ = ComAbi.Release(callbackObject);
            }
        }
    }

}
