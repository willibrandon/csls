using Csls.Debugger.Contracts;
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
    private readonly Dictionary<(int ThreadId, int FrameIndex), ManagedFrameHandle> _frames = [];
    private readonly Dictionary<(int FrameId, ManagedScopeKind Kind), ManagedScopeHandle> _scopes = [];
    private nint _corDebug;
    private nint _debugProcess;
    private int _nextFrameId;
    private int _nextVariablesReference;
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

    /// <summary>
    /// Stops all managed threads at a runtime-consistent inspection point.
    /// </summary>
    internal void Pause()
    {
        ClearFrameHandles();
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Stop(dwTimeoutIgnored: 0),
            "ICorDebugController.Stop");
    }

    /// <summary>
    /// Resumes all managed threads from the current debugger stop.
    /// </summary>
    internal void Continue()
    {
        ClearFrameHandles();
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).Continue(fIsOutOfBand: 0),
            "ICorDebugController.Continue");
    }

    /// <summary>
    /// Enumerates managed threads while the target is stopped.
    /// </summary>
    /// <returns>A bounded snapshot of current managed threads.</returns>
    internal unsafe IReadOnlyList<DebugThreadInfo> GetThreads()
    {
        const int maximumThreadCount = 4096;
        nint enumerator = 0;
        nint* enumeratorAddress = &enumerator;
        CorDebugHResult.ThrowIfFailed(
            new ICorDebugControllerAbi(_debugProcess).EnumerateThreads((nint)enumeratorAddress),
            "ICorDebugController.EnumerateThreads");
        enumerator = Volatile.Read(ref *enumeratorAddress);
        if (enumerator == 0)
        {
            throw new InvalidOperationException(
                "ICorDebugController.EnumerateThreads returned no enumerator.");
        }

        try
        {
            var result = new List<DebugThreadInfo>();
            var api = new ICorDebugThreadEnumAbi(enumerator);
            while (result.Count < maximumThreadCount)
            {
                nint thread = 0;
                uint fetched = 0;
                nint* threadAddress = &thread;
                uint* fetchedAddress = &fetched;
                int nextResult = api.Next(1, (nint)threadAddress, (nint)fetchedAddress);
                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugThreadEnum.Next");
                thread = Volatile.Read(ref *threadAddress);
                fetched = Volatile.Read(ref *fetchedAddress);
                if (fetched == 0)
                {
                    break;
                }

                try
                {
                    uint threadId = 0;
                    uint* threadIdAddress = &threadId;
                    CorDebugHResult.ThrowIfFailed(
                        new ICorDebugThreadAbi(thread).GetID((nint)threadIdAddress),
                        "ICorDebugThread.GetID");
                    int id = checked((int)threadId);
                    result.Add(new DebugThreadInfo(id, $"Thread {id}"));
                }
                finally
                {
                    if (thread != 0)
                    {
                        _ = ComAbi.Release(thread);
                    }
                }
            }

            if (result.Count == maximumThreadCount)
            {
                throw new InvalidOperationException(
                    $"The target exceeds the managed-thread limit of {maximumThreadCount}.");
            }

            return result;
        }
        finally
        {
            _ = ComAbi.Release(enumerator);
        }
    }

    /// <summary>
    /// Enumerates a page of managed frames and retains generation-bound frame handles.
    /// </summary>
    /// <param name="threadId">The runtime thread identifier.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <param name="startFrame">The zero-based first frame to return.</param>
    /// <param name="levels">The maximum count, or zero for all remaining frames.</param>
    /// <returns>The selected stack page and complete frame count.</returns>
    internal unsafe DebugStackTrace GetStackTrace(
        int threadId,
        DebugStopGeneration generation,
        int startFrame,
        int levels)
    {
        const int maximumFrameCount = 4096;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(levels);

        const int endOfStackHResult = 0x00131324;
        const int maximumWalkCount = 16 * 1024;
        nint thread = 0;
        nint thread3 = 0;
        nint stackWalk = 0;
        try
        {
            nint* threadAddress = &thread;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugProcessAbi(_debugProcess).GetThread(
                    checked((uint)threadId),
                    (nint)threadAddress),
                "ICorDebugProcess.GetThread");
            thread = Volatile.Read(ref *threadAddress);
            if (thread == 0)
            {
                throw new InvalidOperationException($"Managed thread {threadId} no longer exists.");
            }

            if (!ComAbi.TryQueryInterface(thread, ICorDebugThread3Abi.InterfaceId, out thread3))
            {
                throw new InvalidOperationException(
                    "The target runtime does not expose ICorDebugThread3 stack walking.");
            }

            nint* stackWalkAddress = &stackWalk;
            CorDebugHResult.ThrowIfFailed(
                new ICorDebugThread3Abi(thread3).CreateStackWalk((nint)stackWalkAddress),
                "ICorDebugThread3.CreateStackWalk");
            stackWalk = Volatile.Read(ref *stackWalkAddress);
            if (stackWalk == 0)
            {
                throw new InvalidOperationException(
                    "ICorDebugThread3.CreateStackWalk returned no stack walker.");
            }

            List<DebugStackFrameInfo> frames = [];
            int frameIndex = 0;
            var walker = new ICorDebugStackWalkAbi(stackWalk);
            for (int walkIndex = 0; walkIndex < maximumWalkCount; walkIndex++)
            {
                nint frame = 0;
                nint* frameAddress = &frame;
                int frameResult = walker.GetFrame((nint)frameAddress);
                CorDebugHResult.ThrowIfFailed(frameResult, "ICorDebugStackWalk.GetFrame");
                frame = Volatile.Read(ref *frameAddress);
                if (frameResult == 0 && frame != 0)
                {
                    bool selected = frameIndex >= startFrame &&
                        (levels == 0 || frames.Count < levels);
                    if (selected)
                    {
                        frames.Add(CreateStackFrame(
                            threadId,
                            frameIndex,
                            generation,
                            frame));
                        frame = 0;
                    }

                    frameIndex++;
                }

                if (frame != 0)
                {
                    _ = ComAbi.Release(frame);
                }

                if (frameIndex > maximumFrameCount)
                {
                    throw new InvalidOperationException(
                        $"The target exceeds the managed-frame limit of {maximumFrameCount}.");
                }

                int nextResult = walker.Next();
                if (nextResult == endOfStackHResult)
                {
                    return new DebugStackTrace(frames, frameIndex);
                }

                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugStackWalk.Next");
            }

            throw new InvalidOperationException(
                $"The target exceeds the stack-walk limit of {maximumWalkCount}.");
        }
        finally
        {
            if (stackWalk != 0)
            {
                _ = ComAbi.Release(stackWalk);
            }

            if (thread3 != 0)
            {
                _ = ComAbi.Release(thread3);
            }

            if (thread != 0)
            {
                _ = ComAbi.Release(thread);
            }
        }
    }

    /// <summary>
    /// Creates argument and local scopes for a generation-bound managed frame.
    /// </summary>
    /// <param name="frameId">The session-local frame handle.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <returns>The runtime-backed scopes available for the frame.</returns>
    internal IReadOnlyList<DebugScopeInfo> GetScopes(
        int frameId,
        DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = GetFrame(frameId, generation);
        if (!ComAbi.TryQueryInterface(
            frame.Pointer,
            ICorDebugILFrameAbi.InterfaceId,
            out nint ilFrame))
        {
            return [];
        }

        _ = ComAbi.Release(ilFrame);
        return
        [
            CreateScope(frame, ManagedScopeKind.Arguments, "Arguments"),
            CreateScope(frame, ManagedScopeKind.Locals, "Locals")
        ];
    }

    /// <summary>
    /// Enumerates and formats one page of values from a generation-bound frame scope.
    /// </summary>
    /// <param name="variablesReference">The session-local variable-container handle.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <param name="start">The zero-based first value to return.</param>
    /// <param name="count">The maximum count, or zero for all remaining values.</param>
    /// <returns>The requested immediate variable page.</returns>
    internal IReadOnlyList<DebugVariableInfo> GetVariables(
        int variablesReference,
        DebugStopGeneration generation,
        int start,
        int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ManagedScopeHandle scope = _scopes.Values.FirstOrDefault(
            candidate => candidate.Id == variablesReference)
            ?? throw new InvalidOperationException(
                $"Variable reference {variablesReference} is stale or unknown.");
        if (scope.Generation != generation)
        {
            throw new InvalidOperationException(
                $"Variable reference {variablesReference} belongs to a retired stop generation.");
        }

        ManagedFrameHandle frame = GetFrame(scope.FrameId, generation);
        IReadOnlyDictionary<int, string> names = GetVariableNames(frame, scope.Kind);
        return EnumerateValues(frame.Pointer, scope.Kind, names, start, count);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await TerminateProcessAsync(_process, CancellationToken.None).ConfigureAwait(false);
        nint corDebug = Interlocked.Exchange(ref _corDebug, 0);
        nint debugProcess = Interlocked.Exchange(ref _debugProcess, 0);
        await _actor.InvokeAsync(
            cancellationToken =>
            {
                _ = cancellationToken;
                ClearFrameHandles();
                ReleaseRuntimeReferences(corDebug, debugProcess);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
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
                ReleaseRuntimeReferences(corDebug, debugProcess);

                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private unsafe DebugStackFrameInfo CreateStackFrame(
        int threadId,
        int frameIndex,
        DebugStopGeneration generation,
        nint frame)
    {
        nint ilFrame = 0;
        uint methodToken = 0;
        uint ilOffset = 0;
        ManagedFrameLocation location = new()
        {
            Name = "[External Code]",
            Line = 0,
            Column = 0
        };
        try
        {
            if (ComAbi.TryQueryInterface(frame, ICorDebugILFrameAbi.InterfaceId, out ilFrame))
            {
                int mappingResult = 0;
                uint* methodTokenAddress = &methodToken;
                uint* ilOffsetAddress = &ilOffset;
                int* mappingResultAddress = &mappingResult;
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugFrameAbi(frame).GetFunctionToken((nint)methodTokenAddress),
                    "ICorDebugFrame.GetFunctionToken");
                CorDebugHResult.ThrowIfFailed(
                    new ICorDebugILFrameAbi(ilFrame).GetIP(
                        (nint)ilOffsetAddress,
                        (nint)mappingResultAddress),
                    "ICorDebugILFrame.GetIP");
                methodToken = Volatile.Read(ref *methodTokenAddress);
                ilOffset = Volatile.Read(ref *ilOffsetAddress);
                location = PortablePdbFrameResolver.Resolve(frame, methodToken, ilOffset);
            }
        }
        finally
        {
            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }
        }

        (int ThreadId, int FrameIndex) key = (threadId, frameIndex);
        if (_frames.TryGetValue(key, out ManagedFrameHandle? existing))
        {
            _ = ComAbi.Release(frame);
        }
        else
        {
            existing = new ManagedFrameHandle
            {
                Id = checked(++_nextFrameId),
                Generation = generation,
                Pointer = frame,
                MethodToken = methodToken,
                IlOffset = ilOffset,
                ModulePath = location.ModulePath
            };
            _frames.Add(key, existing);
        }

        return new DebugStackFrameInfo(
            existing.Id,
            location.Name,
            location.SourcePath,
            location.Line,
            location.Column);
    }

    private void ClearFrameHandles()
    {
        foreach (ManagedFrameHandle frame in _frames.Values)
        {
            _ = ComAbi.Release(frame.Pointer);
        }

        _frames.Clear();
        _scopes.Clear();
    }

    private ManagedFrameHandle GetFrame(int frameId, DebugStopGeneration generation)
    {
        ManagedFrameHandle frame = _frames.Values.FirstOrDefault(
            candidate => candidate.Id == frameId)
            ?? throw new InvalidOperationException($"Frame {frameId} is stale or unknown.");
        if (frame.Generation != generation)
        {
            throw new InvalidOperationException(
                $"Frame {frameId} belongs to a retired stop generation.");
        }

        return frame;
    }

    private DebugScopeInfo CreateScope(
        ManagedFrameHandle frame,
        ManagedScopeKind kind,
        string name)
    {
        (int FrameId, ManagedScopeKind Kind) key = (frame.Id, kind);
        if (!_scopes.TryGetValue(key, out ManagedScopeHandle? scope))
        {
            scope = new ManagedScopeHandle
            {
                Id = checked(++_nextVariablesReference),
                FrameId = frame.Id,
                Generation = frame.Generation,
                Kind = kind
            };
            _scopes.Add(key, scope);
        }

        return new DebugScopeInfo(name, scope.Id, Expensive: false);
    }

    private static IReadOnlyDictionary<int, string> GetVariableNames(
        ManagedFrameHandle frame,
        ManagedScopeKind kind)
    {
        try
        {
            return kind == ManagedScopeKind.Arguments
                ? PortablePdbVariableNameResolver.GetArguments(frame.ModulePath, frame.MethodToken)
                : PortablePdbVariableNameResolver.GetLocals(
                    frame.ModulePath,
                    frame.MethodToken,
                    frame.IlOffset);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return new Dictionary<int, string>();
        }
    }

    private static unsafe List<DebugVariableInfo> EnumerateValues(
        nint frame,
        ManagedScopeKind kind,
        IReadOnlyDictionary<int, string> names,
        int start,
        int count)
    {
        const int maximumValueCount = 64 * 1024;
        nint ilFrame = 0;
        nint enumerator = 0;
        try
        {
            ilFrame = ComAbi.QueryInterface(frame, ICorDebugILFrameAbi.InterfaceId);
            nint* enumeratorAddress = &enumerator;
            var api = new ICorDebugILFrameAbi(ilFrame);
            int enumerateResult = kind == ManagedScopeKind.Arguments
                ? api.EnumerateArguments((nint)enumeratorAddress)
                : api.EnumerateLocalVariables((nint)enumeratorAddress);
            CorDebugHResult.ThrowIfFailed(enumerateResult, $"ICorDebugILFrame.Enumerate{kind}");
            enumerator = Volatile.Read(ref *enumeratorAddress);
            if (enumerator == 0)
            {
                throw new InvalidOperationException(
                    $"ICorDebugILFrame.Enumerate{kind} returned no enumerator.");
            }

            List<DebugVariableInfo> result = [];
            var values = new ICorDebugValueEnumAbi(enumerator);
            for (int index = 0; index < maximumValueCount; index++)
            {
                nint value = 0;
                uint fetched = 0;
                nint* valueAddress = &value;
                uint* fetchedAddress = &fetched;
                int nextResult = values.Next(1, (nint)valueAddress, (nint)fetchedAddress);
                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugValueEnum.Next");
                value = Volatile.Read(ref *valueAddress);
                fetched = Volatile.Read(ref *fetchedAddress);
                if (fetched == 0)
                {
                    return result;
                }

                try
                {
                    if (index >= start && (count == 0 || result.Count < count))
                    {
                        ManagedValueDisplay display = CorDebugValueFormatter.Format(value);
                        string name = names.TryGetValue(index, out string? sourceName) &&
                            !string.IsNullOrEmpty(sourceName)
                                ? sourceName
                                : kind == ManagedScopeKind.Arguments
                                    ? $"argument {index}"
                                    : $"local {index}";
                        result.Add(new DebugVariableInfo(
                            name,
                            display.Value,
                            display.Type,
                            VariablesReference: 0));
                    }
                }
                finally
                {
                    if (value != 0)
                    {
                        _ = ComAbi.Release(value);
                    }
                }

                if (count > 0 && result.Count == count)
                {
                    return result;
                }
            }

            throw new InvalidOperationException(
                $"The scope exceeds the debugger value limit of {maximumValueCount}.");
        }
        finally
        {
            if (enumerator != 0)
            {
                _ = ComAbi.Release(enumerator);
            }

            if (ilFrame != 0)
            {
                _ = ComAbi.Release(ilFrame);
            }
        }
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
