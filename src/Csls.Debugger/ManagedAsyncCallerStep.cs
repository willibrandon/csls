using Csls.Debugger.Interop;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Owns a frame-bound step back to an asynchronous caller that is still executing on the stack.
/// </summary>
internal sealed class ManagedAsyncCallerStep
{
    private nint _stepper;
    private nint _identity;

    /// <summary>
    /// Gets whether the physical caller has an active return step.
    /// </summary>
    internal bool IsActive => _stepper != 0;

    /// <summary>
    /// Identifies a completion belonging to the retained caller stepper.
    /// </summary>
    internal bool Owns(nint identity) => _identity != 0 && identity == _identity;

    /// <summary>
    /// Arms the nearest authored async caller when the active frame is a compiler-recorded state machine.
    /// </summary>
    internal unsafe void Prepare(nint thread, Func<nint, CorDebugLoadedModule?> resolveModule,
        Action<nint> configureStepper)
    {
        Clear();
        nint frame = 0;
        try
        {
            int result = new ICorDebugThreadAbi(thread).GetActiveFrame((nint)(&frame));
            frame = Volatile.Read(ref frame);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugThread.GetActiveFrame");
            if (frame == 0 || !IsStateMachine(frame, resolveModule))
            {
                return;
            }

            for (int depth = 0; depth < 1024; depth++)
            {
                nint caller = 0;
                result = new ICorDebugFrameAbi(frame).GetCaller((nint)(&caller));
                caller = Volatile.Read(ref caller);
                Release(frame);
                frame = caller;
                CorDebugHResult.ThrowIfFailed(result, "ICorDebugFrame.GetCaller");
                if (frame == 0)
                {
                    return;
                }

                if (IsStateMachine(frame, resolveModule))
                {
                    nint stepper = 0;
                    result = new ICorDebugFrameAbi(frame).CreateStepper((nint)(&stepper));
                    _stepper = Volatile.Read(ref stepper);
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugFrame.CreateStepper");
                    if (_stepper == 0)
                    {
                        throw new InvalidOperationException("The async caller returned no stepper.");
                    }

                    configureStepper(_stepper);
                    _identity = ComAbi.GetIdentity(_stepper);
                    CorDebugHResult.ThrowIfFailed(new ICorDebugStepperAbi(_stepper).Step(0),
                        "ICorDebugStepper.Step");
                    return;
                }
            }
        }
        catch
        {
            Clear();
            throw;
        }
        finally
        {
            Release(frame);
        }
    }

    /// <summary>
    /// Releases the caller step when source stepping completes, is interrupted, or the runtime exits.
    /// </summary>
    internal void Clear(bool runtimeAvailable = true)
    {
        nint stepper = Interlocked.Exchange(ref _stepper, 0);
        Release(Interlocked.Exchange(ref _identity, 0));
        if (stepper != 0)
        {
            if (runtimeAvailable)
            {
                _ = new ICorDebugStepperAbi(stepper).Deactivate();
            }

            Release(stepper);
        }
    }

    private static unsafe bool IsStateMachine(nint frame, Func<nint, CorDebugLoadedModule?> resolveModule)
    {
        nint function = 0;
        nint module = 0;
        try
        {
            int result = new ICorDebugFrameAbi(frame).GetFunction((nint)(&function));
            function = Volatile.Read(ref function);
            if (result < 0 || function == 0)
            {
                return false;
            }

            uint token = 0;
            CorDebugHResult.ThrowIfFailed(new ICorDebugFunctionAbi(function).GetToken((nint)(&token)),
                "ICorDebugFunction.GetToken");
            token = Volatile.Read(ref token);
            result = new ICorDebugFunctionAbi(function).GetModule((nint)(&module));
            module = Volatile.Read(ref module);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugFunction.GetModule");
            CorDebugLoadedModule? loadedModule = module == 0 ? null : resolveModule(module);
            using DebugSymbolReader? symbols = loadedModule?.OpenSymbols();
            if (symbols?.GetStateMachineKickoffMethod(token) is not uint kickoff)
            {
                return false;
            }

            using PEReader? image = loadedModule?.OpenPeReader();
            if (image is null)
            {
                return false;
            }

            MetadataReader metadata = image.GetMetadataReader();
            MethodDefinition method = metadata.GetMethodDefinition(
                (MethodDefinitionHandle)MetadataTokens.EntityHandle(checked((int)kickoff)));
            return method.GetCustomAttributes().Select(metadata.GetCustomAttribute)
                .Select(attribute => ManagedDebuggerAttributeReader.GetAttributeTypeName(metadata, attribute))
                .Any(static name => name is "System.Runtime.CompilerServices.AsyncStateMachineAttribute" or
                    "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute");
        }
        catch (Exception exception) when (DebugSymbolReader.IsReadFailure(exception))
        {
            return false;
        }
        finally
        {
            Release(module);
            Release(function);
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
