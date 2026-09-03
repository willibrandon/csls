using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Receives unmanaged CoreCLR callback entry points and queues their work.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Breakpoint(nint self, nint appDomain, nint thread, nint breakpoint)
    {
        return QueueCallback(
            self,
            appDomain,
            thread,
            breakpoint,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, ownedThread, ownedBreakpoint, _, cancellationToken) =>
                target.HandleBreakpointAsync(
                    ownedThread,
                    ownedBreakpoint,
                    cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int StepComplete(nint self, nint appDomain, nint thread, nint stepper, int reason)
    {
        return QueueCallback(
            self,
            appDomain,
            thread,
            stepper,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            (target, ownedThread, ownedStepper, _, cancellationToken) =>
                target.HandleStepCompleteAsync(
                    ownedThread,
                    ownedStepper,
                    reason,
                    cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Break(nint self, nint appDomain, nint thread)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Exception(nint self, nint appDomain, nint thread, int unhandled)
    {
        _ = thread;
        _ = unhandled;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EvalComplete(nint self, nint appDomain, nint thread, nint eval)
    {
        return QueueCallback(
            self,
            appDomain,
            thread,
            eval,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, _, ownedEval, _, cancellationToken) =>
                target.HandleEvaluationCompleteAsync(
                    ownedEval,
                    isException: false,
                    cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EvalException(nint self, nint appDomain, nint thread, nint eval)
    {
        return QueueCallback(
            self,
            appDomain,
            thread,
            eval,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, _, ownedEval, _, cancellationToken) =>
                target.HandleEvaluationCompleteAsync(
                    ownedEval,
                    isException: true,
                    cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateProcess(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExitProcess(nint self, nint process)
    {
        return QueueCallback(
            self,
            process,
            thread: 0,
            subject: 0,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: true,
            continueAfterCallback: false,
            callbackOperation: null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateThread(nint self, nint appDomain, nint thread)
    {
        return QueueCallback(
            self,
            appDomain,
            thread,
            subject: 0,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, ownedThread, _, _, cancellationToken) =>
                target.HandleCreateThreadAsync(ownedThread, cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExitThread(nint self, nint appDomain, nint thread)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LoadModule(nint self, nint appDomain, nint module)
    {
        return QueueCallback(
            self,
            appDomain,
            thread: 0,
            module,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, _, ownedModule, _, cancellationToken) =>
                target.HandleLoadModuleAsync(ownedModule, cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UnloadModule(nint self, nint appDomain, nint module)
    {
        return QueueCallback(
            self,
            appDomain,
            thread: 0,
            module,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, _, ownedModule, _, cancellationToken) =>
                target.HandleUnloadModuleAsync(ownedModule, cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LoadClass(nint self, nint appDomain, nint @class)
    {
        return QueueCallback(
            self,
            appDomain,
            thread: 0,
            @class,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, _, ownedClass, _, cancellationToken) =>
                target.HandleLoadClassAsync(ownedClass, cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UnloadClass(nint self, nint appDomain, nint @class)
    {
        _ = @class;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DebuggerError(nint self, nint process, int error, uint errorCode)
    {
        _ = error;
        _ = errorCode;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LogMessage(nint self, nint appDomain, nint thread, int level, nint category, nint message)
    {
        _ = thread;
        _ = level;
        _ = category;
        _ = message;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LogSwitch(nint self, nint appDomain, nint thread, int level, uint reason, nint category, nint parent)
    {
        _ = thread;
        _ = level;
        _ = reason;
        _ = category;
        _ = parent;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateAppDomain(nint self, nint process, nint appDomain)
    {
        _ = appDomain;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExitAppDomain(nint self, nint process, nint appDomain)
    {
        _ = appDomain;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int LoadAssembly(nint self, nint appDomain, nint assembly)
    {
        _ = assembly;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UnloadAssembly(nint self, nint appDomain, nint assembly)
    {
        _ = assembly;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ControlCTrap(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NameChange(nint self, nint appDomain, nint thread)
    {
        return QueueNameChange(self, appDomain, thread);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UpdateModuleSymbols(nint self, nint appDomain, nint module, nint symbolStream)
    {
        return QueueCallback(
            self,
            appDomain,
            thread: 0,
            module,
            symbolStream,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            static (target, _, ownedModule, ownedSymbolStream, cancellationToken) =>
                target.HandleUpdateModuleSymbolsAsync(
                    ownedModule,
                    ownedSymbolStream,
                    cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EditAndContinueRemap(nint self, nint appDomain, nint thread, nint function, int accurate)
    {
        _ = thread;
        _ = function;
        _ = accurate;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int BreakpointSetError(nint self, nint appDomain, nint thread, nint breakpoint, uint error)
    {
        _ = thread;
        _ = breakpoint;
        _ = error;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FunctionRemapOpportunity(
        nint self,
        nint appDomain,
        nint thread,
        nint oldFunction,
        nint newFunction,
        uint oldIlOffset)
    {
        _ = thread;
        _ = oldFunction;
        _ = newFunction;
        _ = oldIlOffset;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateConnection(
        nint self,
        nint process,
        uint connectionId,
        nint connectionName)
    {
        _ = connectionId;
        _ = connectionName;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ChangeConnection(nint self, nint process, uint connectionId)
    {
        _ = connectionId;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DestroyConnection(nint self, nint process, uint connectionId)
    {
        _ = connectionId;
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Exception2(
        nint self,
        nint appDomain,
        nint thread,
        nint frame,
        uint offset,
        int eventType,
        uint flags)
    {
        _ = frame;
        _ = offset;
        _ = flags;
        return QueueCallback(
            self,
            appDomain,
            thread,
            subject: 0,
            auxiliary: 0,
            createsProcess: false,
            exitsProcess: false,
            continueAfterCallback: true,
            (target, ownedThread, _, _, cancellationToken) =>
                target.HandleExceptionAsync(ownedThread, eventType, cancellationToken));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ExceptionUnwind(
        nint self,
        nint appDomain,
        nint thread,
        int eventType,
        uint flags)
    {
        _ = thread;
        _ = eventType;
        _ = flags;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FunctionRemapComplete(
        nint self,
        nint appDomain,
        nint thread,
        nint function)
    {
        _ = thread;
        _ = function;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MdaNotification(
        nint self,
        nint controller,
        nint thread,
        nint mda)
    {
        _ = thread;
        _ = mda;
        return QueueContinue(self, controller, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CustomNotification(nint self, nint thread, nint appDomain)
    {
        _ = thread;
        return QueueContinue(self, appDomain, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int BeforeGarbageCollection(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AfterGarbageCollection(nint self, nint process)
    {
        return QueueContinue(self, process, createsProcess: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DataBreakpoint(
        nint self,
        nint process,
        nint thread,
        nint context,
        uint contextSize)
    {
        _ = thread;
        _ = context;
        _ = contextSize;
        return QueueContinue(self, process, createsProcess: false);
    }
}
