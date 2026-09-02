using Csls.Debugger.Interop;
using System.Diagnostics;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Owns one launched or attached CoreCLR process and its COM debugger resources.
/// </summary>
internal sealed partial class CorDebugDebuggee : IDebuggeeProcess
{
    private readonly DebuggerSessionActor _actor;
    private readonly CorDebugManagedCallback _managedCallback;
    private readonly CorDebugRuntimeStartupRegistration _registration;
    private readonly DbgShimStandardStreams? _standardStreams;
    private readonly TextReader _standardOutput;
    private readonly TextReader _standardError;
    private readonly Process _process;
    private readonly Task<int>? _unixExitCode;
    private readonly bool _ownsProcess;
    private readonly Dictionary<(int ThreadId, int FrameIndex), ManagedFrameHandle> _frames = [];
    private readonly Dictionary<(int FrameId, ManagedScopeKind Kind), ManagedScopeHandle> _scopes = [];
    private nint _corDebug;
    private nint _debugProcess;
    private nint _activeStepper;
    private nint _activeStepperIdentity;
    private int _nextFrameId;
    private int _nextVariablesReference;
    private int _detached;
    private int _disposed;

    private CorDebugDebuggee(
        DebuggerSessionActor actor,
        CorDebugManagedCallback managedCallback,
        CorDebugRuntimeStartupRegistration registration,
        DbgShimStandardStreams? standardStreams,
        Process process,
        Task<int>? unixExitCode,
        bool ownsProcess,
        CorDebugActivationResult activation)
    {
        _actor = actor;
        _managedCallback = managedCallback;
        _registration = registration;
        _standardStreams = standardStreams;
        _standardOutput = standardStreams is null
            ? TextReader.Null
            : CreateReader(standardStreams.StandardOutput);
        _standardError = standardStreams is null
            ? TextReader.Null
            : CreateReader(standardStreams.StandardError);
        _process = process;
        _unixExitCode = unixExitCode;
        _ownsProcess = ownsProcess;
        _corDebug = activation.CorDebug;
        _debugProcess = activation.Process;
    }

    /// <inheritdoc />
    public int Id => _process.Id;

    /// <inheritdoc />
    public string Name => _process.ProcessName;

    /// <inheritdoc />
    public bool OwnsProcess => _ownsProcess;

    /// <inheritdoc />
    public Task CopyStandardOutputAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken) =>
        CopyAsync(_standardOutput, writeAsync, cancellationToken);

    /// <inheritdoc />
    public Task CopyStandardErrorAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken) =>
        CopyAsync(_standardError, writeAsync, cancellationToken);

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        int exitCode;
        if (_unixExitCode is not null)
        {
            exitCode = await _unixExitCode.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exitCode = GetExitCode(_process);
        }

        await _managedCallback.WaitForExitProcessAsync(cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    /// <inheritdoc />
    public Task TerminateAsync(CancellationToken cancellationToken) =>
        TerminateProcessAsync(_process, cancellationToken);

    /// <inheritdoc />
    public async Task DetachAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _detached, 1) != 0)
        {
            return;
        }

        nint corDebug = Interlocked.Exchange(ref _corDebug, 0);
        nint debugProcess = Interlocked.Exchange(ref _debugProcess, 0);
        await _actor.InvokeAsync(
            token =>
            {
                _ = token;
                DetachRuntimeReferences(corDebug, debugProcess);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _detached) == 0)
        {
            if (_ownsProcess)
            {
                await TerminateProcessAsync(_process, CancellationToken.None).ConfigureAwait(false);
                await _managedCallback.WaitForExitProcessAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await DetachAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        nint corDebug = Interlocked.Exchange(ref _corDebug, 0);
        nint debugProcess = Interlocked.Exchange(ref _debugProcess, 0);
        await _actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                ClearFrameHandles();
                CancelStep();
                ReleaseRuntimeReferences(corDebug, debugProcess);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
        _registration.Dispose();
        _managedCallback.Dispose();
        _standardOutput.Dispose();
        _standardError.Dispose();
        if (_standardStreams is not null)
        {
            await _standardStreams.DisposeAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }

    private static StreamReader CreateReader(Stream stream) =>
        new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

    private static async Task CopyAsync(
        TextReader reader,
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            await writeAsync(new string(buffer, 0, count), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ReleaseRuntimeAsync(
        DebuggerSessionActor actor,
        nint corDebug,
        nint debugProcess)
    {
        if (corDebug == 0 && debugProcess == 0)
        {
            return;
        }

        await actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                ReleaseRuntimeReferences(corDebug, debugProcess);

                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DetachRuntimeAsync(
        DebuggerSessionActor actor,
        nint corDebug,
        nint debugProcess)
    {
        if (corDebug == 0 && debugProcess == 0)
        {
            return;
        }

        await actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                DetachRuntimeReferences(corDebug, debugProcess);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static void ReleaseRuntimeReferences(nint corDebug, nint debugProcess)
    {
        if (debugProcess != 0)
        {
            _ = ComAbi.Release(debugProcess);
        }

        if (corDebug != 0)
        {
            _ = new ICorDebugAbi(corDebug).Terminate();
            _ = ComAbi.Release(corDebug);
        }
    }

    private static void DetachRuntimeReferences(nint corDebug, nint debugProcess)
    {
        try
        {
            if (debugProcess != 0)
            {
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugControllerAbi(debugProcess).Detach(),
                    "ICorDebugController.Detach");
            }
        }
        finally
        {
            ReleaseRuntimeReferences(corDebug, debugProcess);
        }
    }

    private static int GetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static async Task TerminateProcessAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
