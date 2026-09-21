using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Owns temporary resume breakpoints and the exact consumer of a suspended asynchronous operation.
/// </summary>
internal sealed class ManagedAsyncConsumerStep
{
    private readonly ManagedContinuationObjectReader _reader;
    private readonly Func<nint, uint, uint, (nint Breakpoint, nint Identity)> _createBreakpoint;
    private readonly List<(nint Breakpoint, nint Identity)> _breakpoints = [];
    private readonly HashSet<nint> _sourceBreakpointIdentities = [];
    private nint _consumerBoxHandle;

    /// <summary>
    /// Gets whether the selected consumer has retained resumption breakpoints.
    /// </summary>
    internal bool IsActive => _breakpoints.Count != 0;

    /// <summary>
    /// Creates a consumer tracker with stopped-process field access and breakpoint allocation.
    /// </summary>
    internal ManagedAsyncConsumerStep(ManagedContinuationObjectReader reader,
        Func<nint, uint, uint, (nint Breakpoint, nint Identity)> createBreakpoint)
    {
        _reader = reader;
        _createBreakpoint = createBreakpoint;
    }

    /// <summary>
    /// Arms source resumption locations for the consumer registered on the iterator promise or async task.
    /// </summary>
    internal void Prepare(nint stateMachine, bool includeTasks)
    {
        Clear();
        nint consumerBox = 0;
        nint consumer = 0;
        try
        {
            consumerBox = ReadConsumerBox(stateMachine, includeTasks);
            if (consumerBox == 0)
            {
                return;
            }

            consumer = _reader.ReadField(consumerBox, "StateMachine", "AsyncStateMachineBox`1");
            if (consumer == 0)
            {
                return;
            }

            (CorDebugLoadedModule module, uint typeToken) = _reader.ResolveValue(consumer);
            using PEReader? image = module.OpenPeReader();
            using DebugSymbolReader? symbols = module.OpenSymbols();
            if (image is null || symbols is null)
            {
                return;
            }

            MetadataReader metadata = image.GetMetadataReader();
            TypeDefinition type = metadata.GetTypeDefinition(
                MetadataTokens.TypeDefinitionHandle(checked((int)(typeToken & 0x00ffffff))));
            foreach (uint token in type.GetMethods().Select(static handle => checked((uint)MetadataTokens.GetToken(handle))))
            {
                if (symbols.GetStateMachineKickoffMethod(token) is null)
                {
                    continue;
                }

                var resumeLocations = new HashSet<(uint Token, uint Offset)>();
                var sourceLocations = new HashSet<(uint Token, uint Offset)>();
                IReadOnlyList<ManagedSequencePoint> sourcePoints = includeTasks
                    ? []
                    : symbols.GetSequencePoints(token);
                foreach (ManagedAsyncAwaitPoint point in symbols.GetAsyncAwaitPoints(token))
                {
                    resumeLocations.Add((point.ResumeMethodToken, point.ResumeOffset));
                    if (!includeTasks)
                    {
                        AddConsumerSourceLocations(sourcePoints, point, sourceLocations);
                    }
                }

                if (resumeLocations.Count == 0)
                {
                    return;
                }

                _consumerBoxHandle = _reader.CreateHandle(consumerBox);
                if (_consumerBoxHandle == 0)
                {
                    return;
                }

                foreach ((uint methodToken, uint offset) in resumeLocations)
                {
                    _breakpoints.Add(_createBreakpoint(module.Pointer, methodToken, offset));
                }

                foreach ((uint methodToken, uint offset) in sourceLocations.Except(resumeLocations))
                {
                    (nint breakpoint, nint identity) = _createBreakpoint(module.Pointer, methodToken, offset);
                    _breakpoints.Add((breakpoint, identity));
                    _sourceBreakpointIdentities.Add(identity);
                }

                return;
            }
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception) ||
            exception is InvalidOperationException or ArgumentException)
        {
            Clear();
        }
        finally
        {
            Release(consumer);
            Release(consumerBox);
        }
    }

    private static void AddConsumerSourceLocations(
        IReadOnlyList<ManagedSequencePoint> sourcePoints,
        ManagedAsyncAwaitPoint awaitPoint,
        HashSet<(uint Token, uint Offset)> locations)
    {
        ManagedSequencePoint? awaitSource = sourcePoints.LastOrDefault(
            point => point.IlOffset >= 0 && (uint)point.IlOffset <= awaitPoint.YieldOffset);
        if (awaitSource is null)
        {
            return;
        }

        // Await-foreach resumes inside compiler-generated code and then branches
        // back to an earlier sequence point on the authored loop line. A thread-
        // bound runtime stepper can be lost across that continuation; source
        // breakpoints preserve the selected consumer across the branch.
        IEnumerable<ManagedSequencePoint> loopSourcePoints = sourcePoints.Where(point =>
            point.IlOffset >= 0 && (uint)point.IlOffset < awaitPoint.YieldOffset &&
            point.StartLine == awaitSource.StartLine &&
            StringComparer.Ordinal.Equals(point.SourcePath, awaitSource.SourcePath));
        foreach (ManagedSequencePoint point in loopSourcePoints)
        {
            locations.Add((point.MethodToken, checked((uint)point.IlOffset)));
        }
    }

    private nint ReadConsumerBox(nint stateMachine, bool includeTasks)
    {
        nint promise = 0;
        nint builder = 0;
        nint task = 0;
        try
        {
            promise = _reader.ReadField(stateMachine, "<>v__promiseOfValueOrEnd");
            if (promise != 0)
            {
                return _reader.ReadField(promise, "_continuationState", "ManualResetValueTaskSourceCore`1");
            }

            if (!includeTasks)
            {
                return 0;
            }

            builder = _reader.ReadField(stateMachine, "<>t__builder");
            if (builder == 0)
            {
                return 0;
            }

            foreach (string type in new[] { "AsyncTaskMethodBuilder`1", "AsyncTaskMethodBuilder",
                "AsyncValueTaskMethodBuilder`1", "AsyncValueTaskMethodBuilder" })
            {
                task = _reader.ReadField(builder, "m_task", type);
                if (task != 0)
                {
                    return ReadTaskConsumerBox(task);
                }
            }

            return 0;
        }
        finally
        {
            Release(task);
            Release(builder);
            Release(promise);
        }
    }

    private nint ReadTaskConsumerBox(nint task)
    {
        nint continuation = _reader.ReadField(task, "m_continuationObject", "Task");
        try
        {
            if (continuation == 0)
            {
                return 0;
            }

            // Captured schedulers and synchronization contexts retain an action
            // wrapper, while default awaits can retain the state-machine box itself.
            foreach ((string field, string type) in new[] { ("m_action", "AwaitTaskContinuation"), ("_target", "Delegate") })
            {
                nint next = _reader.ReadField(continuation, field, type);
                if (next != 0)
                {
                    Release(continuation);
                    continuation = next;
                }
            }

            nint owned = continuation;
            continuation = 0;
            return owned;
        }
        finally
        {
            Release(continuation);
        }
    }

    /// <summary>
    /// Identifies a callback from an owned consumer resumption breakpoint.
    /// </summary>
    internal bool Owns(nint breakpoint)
    {
        if (_breakpoints.Count == 0)
        {
            return false;
        }

        nint identity = ComAbi.GetIdentity(breakpoint);
        try
        {
            return _breakpoints.Any(candidate => candidate.Identity == identity);
        }
        finally
        {
            Release(identity);
        }
    }

    /// <summary>
    /// Identifies an authored consumer statement rather than a compiler-generated await resume.
    /// </summary>
    internal bool IsSourceBreakpoint(nint breakpoint)
    {
        nint identity = ComAbi.GetIdentity(breakpoint);
        try
        {
            return _sourceBreakpointIdentities.Contains(identity);
        }
        finally
        {
            Release(identity);
        }
    }

    /// <summary>
    /// Gets whether an iterator consumer has authored source locations to visit after resumption.
    /// </summary>
    internal bool HasSourceBreakpoints => _sourceBreakpointIdentities.Count != 0;

    /// <summary>
    /// Compares the resumed state machine with the selected consumer's current storage after collection.
    /// </summary>
    internal bool Matches(nint stateMachine)
    {
        nint consumer = 0;
        try
        {
            consumer = _reader.ReadField(_consumerBoxHandle, "StateMachine", "AsyncStateMachineBox`1");
            if (consumer == 0)
            {
                return false;
            }

            ulong address = _reader.GetAddress(consumer);
            return address != 0 && address == _reader.GetAddress(stateMachine);
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception) ||
            exception is InvalidOperationException or ArgumentException)
        {
            return false;
        }
        finally
        {
            Release(consumer);
        }
    }

    /// <summary>
    /// Retires every owned breakpoint and the consumer handle when stepping stops or the target exits.
    /// </summary>
    internal void Clear(bool runtimeAvailable = true)
    {
        foreach ((nint breakpoint, nint identity) in _breakpoints)
        {
            if (runtimeAvailable)
            {
                _ = new ICorDebugBreakpointAbi(breakpoint).Activate(0);
            }

            Release(identity);
            Release(breakpoint);
        }

        _breakpoints.Clear();
        _sourceBreakpointIdentities.Clear();
        nint handle = Interlocked.Exchange(ref _consumerBoxHandle, 0);
        if (handle != 0)
        {
            if (runtimeAvailable)
            {
                _ = new ICorDebugHandleValueAbi(handle).Dispose();
            }

            Release(handle);
        }
    }

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
