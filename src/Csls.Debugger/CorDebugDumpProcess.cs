using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;
using System.Runtime.InteropServices.Marshalling;

namespace Csls.Debugger;

/// <summary>
/// Owns a public CoreCLR virtual process over immutable captured memory with no live-process attachment.
/// </summary>
public sealed unsafe class CorDebugDumpProcess : IDisposable
{
    private readonly CorDebugDumpCallbacks _callbacks;
    private readonly Lock _gate = new();
    private readonly CorDebugDumpValues _values;
    private readonly CorDebugDumpArrayReader _arrays;
    private nint _debugging;
    private nint _process;
    private nint _dataTarget;
    private nint _libraryProvider;

    /// <summary>
    /// Opens the captured runtime through the public debugger shim and retains its dump callbacks.
    /// </summary>
    /// <param name="source">The immutable source, owned by the caller until this process is disposed.</param>
    /// <param name="runtimeBaseAddress">The captured CoreCLR module's virtual base address.</param>
    /// <param name="values">The session-owned logical expansion paths preserved across native cache replacement.</param>
    /// <param name="cancellationToken">Cancels activation through captured-memory callbacks.</param>
    public CorDebugDumpProcess(ICorDebugDumpSource source, ulong runtimeBaseAddress,
        CorDebugDumpValues? values = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfZero(runtimeBaseAddress);
        _callbacks = new CorDebugDumpCallbacks(source);
        _values = values ?? new CorDebugDumpValues();
        _arrays = new CorDebugDumpArrayReader(_values);
        var activation = new CorDebugDumpReadOperation(cancellationToken, null);
        _callbacks.Operation = activation;
        try
        {
            activation.ThrowIfInterrupted();
            DbgShimLibrary.VerifyPlatformSupport();
            Guid classId = new("BACC578D-FBDD-48A4-969F-02D932B74634");
            Guid interfaceId = new("D28F3C5A-9634-4206-A509-477552EEFB10");
            CorDebugHResult.ThrowIfFailed(DbgShimNativeMethods.ClrCreateInstance(in classId, in interfaceId, out _debugging),
                "CLRCreateInstance");
            _dataTarget = (nint)ComInterfaceMarshaller<ICorDebugDumpDataTarget>.ConvertToUnmanaged(_callbacks);
            _libraryProvider = (nint)ComInterfaceMarshaller<ICorDebugDumpLibraryProvider>.ConvertToUnmanaged(_callbacks);
            var maximumVersion = new ClrDebuggingVersion { _major = 4 };
            ClrDebuggingVersion version = default;
            Guid processId = ICorDebugProcessAbi.InterfaceId;
            uint flags = 0;
            nint process = 0;
            nint* table = *(nint**)_debugging;
            var open = (delegate* unmanaged[Stdcall]<nint, ulong, nint, nint, ClrDebuggingVersion*, Guid*, nint*,
                ClrDebuggingVersion*, uint*, int>)table[3];
            int result = open(_debugging, runtimeBaseAddress, _dataTarget, _libraryProvider, &maximumVersion,
                &processId, &process, &version, &flags);
            _process = Volatile.Read(ref process);
            activation.ThrowIfInterrupted();
            if (result < 0 && _callbacks.LastFailure is { } failure)
            {
                throw new InvalidOperationException($"Opening the captured CoreCLR process failed (0x{result:X8}): {failure.Message}", failure);
            }

            CorDebugHResult.ThrowIfFailed(result, "ICLRDebugging.OpenVirtualProcess");
            if (_process == 0)
            {
                throw new InvalidOperationException("CoreCLR returned no captured process interface.");
            }
        }
        catch
        {
            _callbacks.Operation = null;
            Dispose();
            throw;
        }
        finally
        {
            _callbacks.Operation = null;
        }
    }

    /// <summary>
    /// Recovers physical argument and local values from one exact captured managed frame.
    /// </summary>
    /// <param name="threadId">The captured operating-system thread identifier.</param>
    /// <param name="stackPointer">The captured frame's stack pointer.</param>
    /// <param name="methodToken">The captured method definition token.</param>
    /// <param name="arguments">Whether to inspect arguments rather than locals.</param>
    /// <param name="start">The first physical value slot to inspect.</param>
    /// <param name="count">The page size, or zero for all remaining slots within the response budget.</param>
    /// <param name="cancellationToken">Cancels between native stack and value reads.</param>
    /// <param name="progress">Observes bounded captured-memory work inside native inspection.</param>
    /// <returns>The selected captured frame and its immediate runtime values.</returns>
    public CorDebugDumpFrameInfo ReadFrame(uint threadId, ulong stackPointer, uint methodToken,
        bool arguments, int start, int count, CancellationToken cancellationToken,
        IProgress<DebugDumpReadProgress>? progress = null) =>
        ExecuteRead(() => ReadFrameCore(threadId, stackPointer, methodToken, arguments, start, count, cancellationToken),
            cancellationToken, progress);

    /// <summary>
    /// Reacquires a captured expansion path and reads a bounded page of its children.
    /// </summary>
    /// <param name="request">The logical handle and requested page.</param>
    /// <param name="cancellationToken">Cancels captured native reads and page traversal.</param>
    /// <returns>The requested immutable captured child values.</returns>
    public IReadOnlyList<DebugVariableInfo> ReadChildren(DebugVariablesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteRead(() =>
        {
            if (!Enum.IsDefined(request.Filter))
            {
                throw new ArgumentOutOfRangeException(nameof(request));
            }
            if (request.AllowTargetCodeExecution)
            {
                throw new NotSupportedException("Captured values support read-only inspection.");
            }
            CorDebugDumpValuePath path = _values.Get(request.VariablesReference);
            return ReadFrameCore(path.ThreadId, path.StackPointer, path.MethodToken, path.Arguments,
                request.Start, request.Count, cancellationToken, request).Values;
        }, cancellationToken, request.DumpReadProgress);
    }

    private T ExecuteRead<T>(Func<T> read, CancellationToken cancellationToken, IProgress<DebugDumpReadProgress>? progress)
    {
        lock (_gate)
        {
            if (_callbacks.Operation is not null)
            {
                throw new InvalidOperationException("A captured-frame read is already active.");
            }

            var operation = new CorDebugDumpReadOperation(cancellationToken, progress);
            int checkpoint = _values.Checkpoint;
            _callbacks.Operation = operation;
            try
            {
                T result;
                try
                {
                    result = read();
                }
                catch
                {
                    // CoreCLR can translate an aborted callback into a generic read failure.
                    operation.ThrowIfInterrupted();
                    throw;
                }

                operation.ThrowIfInterrupted();
                operation.ReportTerminal(DebugDumpReadState.Completed);
                return result;
            }
            catch (OperationCanceledException failure) when (cancellationToken.IsCancellationRequested)
            {
                _values.Rollback(checkpoint);
                operation.ReportFailure(DebugDumpReadState.Canceled, failure);
                throw;
            }
            catch (Exception failure)
            {
                _values.Rollback(checkpoint);
                operation.ReportFailure(DebugDumpReadState.Failed, failure);
                throw;
            }
            finally
            {
                _callbacks.Operation = null;
            }
        }
    }

    private CorDebugDumpFrameInfo ReadFrameCore(uint threadId, ulong stackPointer, uint methodToken,
        bool arguments, int start, int count, CancellationToken cancellationToken, DebugVariablesRequest? children = null)
    {
        ObjectDisposedException.ThrowIf(_process == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 4096);
        cancellationToken.ThrowIfCancellationRequested();
        nint thread = 0;
        nint thread3 = 0;
        nint walk = 0;
        try
        {
            CorDebugHResult.ThrowIfFailed(new ICorDebugProcessAbi(_process).GetThread(threadId, (nint)(&thread)),
                "ICorDebugProcess.GetThread");
            thread3 = ComAbi.QueryInterface(Volatile.Read(ref thread), ICorDebugThread3Abi.InterfaceId);
            CorDebugHResult.ThrowIfFailed(new ICorDebugThread3Abi(thread3).CreateStackWalk((nint)(&walk)),
                "ICorDebugThread3.CreateStackWalk");
            var stack = new ICorDebugStackWalkAbi(Volatile.Read(ref walk));
            for (int index = 0; index < 4096; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint frame = 0;
                try
                {
                    CorDebugHResult.ThrowIfFailed(stack.GetFrame((nint)(&frame)), "ICorDebugStackWalk.GetFrame");
                    frame = Volatile.Read(ref frame);
                    if (frame != 0 && ComAbi.TryQueryInterface(frame, ICorDebugILFrameAbi.InterfaceId, out nint ilFrame))
                    {
                        try
                        {
                            uint method = 0;
                            var api = new ICorDebugFrameAbi(frame);
                            CorDebugHResult.ThrowIfFailed(api.GetFunctionToken((nint)(&method)), "ICorDebugFrame.GetFunctionToken");
                            if (Volatile.Read(ref method) == methodToken &&
                                ReadStackPointer(frame) == stackPointer)
                            {
                                var root = new CorDebugDumpValuePath(threadId, stackPointer, methodToken, arguments, 0);
                                return ReadFrame(frame, ilFrame, root, start, count, children, cancellationToken);
                            }
                        }
                        finally
                        {
                            _ = ComAbi.Release(ilFrame);
                        }
                    }
                }
                finally
                {
                    Release(ref frame);
                }

                int next = stack.Next();
                CorDebugHResult.ThrowIfFailed(next, "ICorDebugStackWalk.Next");
                if (next == 1)
                {
                    throw new InvalidOperationException("The requested physical managed frame was not found in the captured thread.");
                }
            }

            throw new InvalidDataException("The captured thread exceeds the offline stack-walk limit of 4096 frames.");
        }
        finally
        {
            Release(ref walk);
            Release(ref thread3);
            Release(ref thread);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_process != 0)
            {
                // For virtual processes, Detach neuters the inspection objects; it never opens a live target.
                _ = new ICorDebugControllerAbi(_process).Detach();
                Release(ref _process);
            }

            Release(ref _debugging);
            Release(ref _libraryProvider);
            Release(ref _dataTarget);
            _callbacks.Dispose();
            GC.KeepAlive(_callbacks);
        }
    }

    private static ulong ReadStackPointer(nint frame)
    {
        nint nativeFrame = ComAbi.QueryInterface(frame, ICorDebugNativeFrameAbi.InterfaceId);
        nint registers = 0;
        try
        {
            CorDebugHResult.ThrowIfFailed(new ICorDebugNativeFrameAbi(nativeFrame).GetRegisterSet((nint)(&registers)),
                "ICorDebugNativeFrame.GetRegisterSet");
            ulong pointer = 0;
            ulong available = 0;
            var api = new ICorDebugRegisterSetAbi(Volatile.Read(ref registers));
            CorDebugHResult.ThrowIfFailed(api.GetRegistersAvailable((nint)(&available)), "ICorDebugRegisterSet.GetRegistersAvailable");
            if ((Volatile.Read(ref available) & (1UL << 1)) == 0)
            {
                throw new InvalidOperationException("The captured frame has no available stack-pointer register.");
            }

            CorDebugHResult.ThrowIfFailed(api.GetRegisters(1UL << 1, 1, (nint)(&pointer)), "ICorDebugRegisterSet.GetRegisters");
            return Volatile.Read(ref pointer);
        }
        finally
        {
            Release(ref registers);
            Release(ref nativeFrame);
        }
    }

    private CorDebugDumpFrameInfo ReadFrame(nint frame, nint ilFrame, CorDebugDumpValuePath root,
        int start, int count, DebugVariablesRequest? children, CancellationToken cancellationToken)
    {
        uint method = 0;
        uint offset = 0;
        int mapping = 0;
        var api = new ICorDebugFrameAbi(frame);
        CorDebugHResult.ThrowIfFailed(api.GetFunctionToken((nint)(&method)), "ICorDebugFrame.GetFunctionToken");
        CorDebugHResult.ThrowIfFailed(new ICorDebugILFrameAbi(ilFrame).GetIP((nint)(&offset), (nint)(&mapping)),
            "ICorDebugILFrame.GetIP");
        IReadOnlyList<DebugVariableInfo> result = children is null
            ? ReadValues(ilFrame, root, start, count, cancellationToken)
            : _arrays.Read(ilFrame, _values.Get(children.VariablesReference), children.VariablesReference,
                start, count, children.Filter, cancellationToken);
        return new CorDebugDumpFrameInfo(Volatile.Read(ref method), root.StackPointer, Volatile.Read(ref offset), result);
    }

    private List<DebugVariableInfo> ReadValues(nint frame, CorDebugDumpValuePath root,
        int start, int count, CancellationToken cancellationToken)
    {
        bool arguments = root.Arguments;
        nint enumerator = 0;
        try
        {
            var api = new ICorDebugILFrameAbi(frame);
            int hr = arguments ? api.EnumerateArguments((nint)(&enumerator)) : api.EnumerateLocalVariables((nint)(&enumerator));
            CorDebugHResult.ThrowIfFailed(hr, "ICorDebugILFrame.EnumerateValues");
            uint total = 0;
            CorDebugHResult.ThrowIfFailed(new ICorDebugEnumAbi(Volatile.Read(ref enumerator)).GetCount((nint)(&total)),
                "ICorDebugEnum.GetCount");
            total = Volatile.Read(ref total);
            if (total > 64 * 1024)
            {
                throw new InvalidDataException("The captured frame exceeds the offline value limit of 65536 slots.");
            }

            int length = start >= total ? 0 : (int)total - start;
            length = count == 0 ? length : Math.Min(count, length);
            if (length > 4096)
            {
                throw new InvalidDataException("The requested captured values exceed the response limit of 4096 slots.");
            }

            List<DebugVariableInfo> result = [];
            for (int index = start; index - start < length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint value = 0;
                string name = $"{(arguments ? "argument" : "local")} {index}";
                try
                {
                    int get = arguments ? api.GetArgument((uint)index, (nint)(&value)) : api.GetLocalVariable((uint)index, (nint)(&value));
                    CorDebugHResult.ThrowIfFailed(get, "ICorDebugILFrame.GetValue");
                    result.Add(_arrays.Describe(Volatile.Read(ref value), name, root with { Slot = index }));
                }
                catch (InvalidOperationException exception) when (CorDebugDumpArrayReader.IsUnavailable(exception))
                {
                    result.Add(CorDebugDumpArrayReader.Unavailable(name, exception));
                }
                finally
                {
                    Release(ref value);
                }
            }

            return result;
        }
        finally
        {
            Release(ref enumerator);
        }
    }

    private static void Release(ref nint pointer)
    {
        nint owned = pointer;
        pointer = 0;
        if (owned != 0)
        {
            _ = ComAbi.Release(owned);
        }
    }
}
