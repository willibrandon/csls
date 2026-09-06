using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Owns temporary resume breakpoints and the exact consumer of a suspended async iterator.
/// </summary>
internal sealed class ManagedAsyncConsumerStep
{
    private readonly ManagedContinuationObjectReader _reader;
    private readonly Func<nint, uint, uint, (nint Breakpoint, nint Identity)> _createBreakpoint;
    private readonly List<(nint Breakpoint, nint Identity)> _breakpoints = [];
    private nint _consumerBoxHandle;

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
    /// Arms source resumption locations for the consumer already registered on the iterator promise.
    /// </summary>
    internal void Prepare(nint stateMachine)
    {
        Clear();
        nint promise = 0;
        nint consumerBox = 0;
        nint consumer = 0;
        try
        {
            promise = _reader.ReadField(stateMachine, "<>v__promiseOfValueOrEnd");
            if (promise == 0)
            {
                return;
            }

            consumerBox = _reader.ReadField(promise, "_continuationState", "ManualResetValueTaskSourceCore`1");
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
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                uint token = checked((uint)MetadataTokens.GetToken(methodHandle));
                if (symbols.GetStateMachineKickoffMethod(token) is null)
                {
                    continue;
                }

                var locations = new HashSet<(uint Token, uint Offset)>();
                foreach (ManagedAsyncAwaitPoint point in symbols.GetAsyncAwaitPoints(token))
                {
                    locations.Add((point.ResumeMethodToken, point.ResumeOffset));
                }

                if (locations.Count == 0)
                {
                    return;
                }

                _consumerBoxHandle = _reader.CreateHandle(consumerBox);
                if (_consumerBoxHandle == 0)
                {
                    return;
                }

                foreach ((uint methodToken, uint offset) in locations)
                {
                    _breakpoints.Add(_createBreakpoint(module.Pointer, methodToken, offset));
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
            Release(promise);
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
