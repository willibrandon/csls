using Csls.Debugger.Interop;
using System.Diagnostics;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Owns one dbgshim-launched CoreCLR process, its COM debugger, and redirected streams.
/// </summary>
internal sealed class CorDebugDebuggee : IDebuggeeProcess
{
    private readonly DebuggerSessionActor _actor;
    private readonly CorDebugManagedCallback _managedCallback;
    private readonly CorDebugRuntimeStartupRegistration _registration;
    private readonly DbgShimStandardStreams _standardStreams;
    private readonly StreamReader _standardOutput;
    private readonly StreamReader _standardError;
    private readonly Process _process;
    private readonly Task<int>? _unixExitCode;
    private nint _corDebug;
    private nint _debugProcess;
    private int _disposed;

    private CorDebugDebuggee(
        DebuggerSessionActor actor,
        CorDebugManagedCallback managedCallback,
        CorDebugRuntimeStartupRegistration registration,
        DbgShimStandardStreams standardStreams,
        Process process,
        Task<int>? unixExitCode,
        CorDebugActivationResult activation)
    {
        _actor = actor;
        _managedCallback = managedCallback;
        _registration = registration;
        _standardStreams = standardStreams;
        _standardOutput = CreateReader(standardStreams.StandardOutput);
        _standardError = CreateReader(standardStreams.StandardError);
        _process = process;
        _unixExitCode = unixExitCode;
        _corDebug = activation.CorDebug;
        _debugProcess = activation.Process;
    }

    /// <inheritdoc />
    public int Id => _process.Id;

    /// <inheritdoc />
    public string Name => _process.ProcessName;

    /// <summary>
    /// Launches a target suspended and activates its CoreCLR debugging interface.
    /// </summary>
    /// <param name="options">The validated target invocation.</param>
    /// <param name="actor">The session actor that owns runtime calls and callbacks.</param>
    /// <param name="cancellationToken">Cancels runtime activation and cleans up the target.</param>
    /// <returns>The live debugger-owned target.</returns>
    internal static async Task<CorDebugDebuggee> LaunchAsync(
        DebuggeeLaunchOptions options,
        DebuggerSessionActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(actor);
        ValidateOptions(options);
        DbgShimLibrary.VerifyPlatformSupport();

        string commandLine = DbgShimCommandLineBuilder.Build(options);
        using var environment = DbgShimEnvironmentBlock.Create(options.Environment);
        var standardStreams = new DbgShimStandardStreams();
        Process? process = null;
        CorDebugManagedCallback? managedCallback = null;
        CorDebugRuntimeStartupRegistration? registration = null;
        Task<int>? unixExitCode = null;
        nint corDebug = 0;
        nint debugProcess = 0;
        try
        {
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

            managedCallback = new CorDebugManagedCallback(actor);
            registration = new CorDebugRuntimeStartupRegistration(processId, managedCallback);
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

            var result = new CorDebugDebuggee(
                actor,
                managedCallback,
                registration,
                standardStreams,
                process,
                unixExitCode,
                activation);
            managedCallback = null;
            registration = null;
            standardStreams = null!;
            process = null;
            unixExitCode = null;
            corDebug = 0;
            debugProcess = 0;
            return result;
        }
        finally
        {
            if (process is not null)
            {
                await TerminateProcessAsync(process, CancellationToken.None).ConfigureAwait(false);
                process.Dispose();
            }

            if (unixExitCode is not null)
            {
                _ = await unixExitCode.ConfigureAwait(false);
            }

            if (corDebug != 0 || debugProcess != 0)
            {
                await ReleaseRuntimeAsync(actor, corDebug, debugProcess).ConfigureAwait(false);
            }

            registration?.Dispose();
            managedCallback?.Dispose();
            if (standardStreams is not null)
            {
                await standardStreams.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

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
        if (_unixExitCode is not null)
        {
            return await _unixExitCode.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <inheritdoc />
    public Task TerminateAsync(CancellationToken cancellationToken) =>
        TerminateProcessAsync(_process, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await TerminateProcessAsync(_process, CancellationToken.None).ConfigureAwait(false);
        await ReleaseRuntimeAsync(
            _actor,
            Interlocked.Exchange(ref _corDebug, 0),
            Interlocked.Exchange(ref _debugProcess, 0)).ConfigureAwait(false);
        _registration.Dispose();
        _managedCallback.Dispose();
        _standardOutput.Dispose();
        _standardError.Dispose();
        await _standardStreams.DisposeAsync().ConfigureAwait(false);
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
                if (debugProcess != 0)
                {
                    _ = ComAbi.Release(debugProcess);
                }

                if (corDebug != 0)
                {
                    _ = new ICorDebugAbi(corDebug).Terminate();
                    _ = ComAbi.Release(corDebug);
                }

                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
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
