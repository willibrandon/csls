using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Observes a test-owned collector's native exceptions from a dedicated external debugger thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNativeDebugObserver
{
    /// <summary>
    /// Attaches to the waiting collector, releases its input gate, and observes native events until exit or cancellation.
    /// </summary>
    /// <param name="process">The caller-owned process waiting for its capture input.</param>
    /// <param name="report">Receives bounded native fault records outside the collector process.</param>
    /// <param name="cancellationToken">Cancels observation and detaches before caller-owned process cleanup.</param>
    /// <param name="phase">Optionally records capture-input release and process exit on the native event thread.</param>
    /// <returns>The native observation lifetime.</returns>
    internal static Task ObserveAsync(Process process, Action<string> report, CancellationToken cancellationToken,
        Action<string>? phase = null) =>
        Task.Factory.StartNew(() => Observe(process, report, phase, cancellationToken), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static unsafe void Observe(Process process, Action<string> report, Action<string>? phase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint processId = checked((uint)process.Id);
        if (DebugActiveProcess(processId) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        bool exited = false;
        try
        {
            // The surrounding process owner controls termination, including failed observation and cancellation.
            if (DebugSetProcessKillOnExit(0) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            bool initialBreakpoint = true;
            int firstChanceReports = 0;
            string? pendingFault = null;
            byte* nativeEvent = stackalloc byte[176];
            int unionOffset = IntPtr.Size == 8 ? 16 : 12;
            while (!exited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (WaitForDebugEventEx(nativeEvent, 100) == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == 121) // ERROR_SEM_TIMEOUT bounds cancellation latency on the owning native thread.
                    {
                        continue;
                    }
                    throw new Win32Exception(error);
                }
                uint code = Unsafe.ReadUnaligned<uint>(nativeEvent);
                uint observedProcess = Unsafe.ReadUnaligned<uint>(nativeEvent + 4);
                uint threadId = Unsafe.ReadUnaligned<uint>(nativeEvent + 8);
                uint disposition = code == 1 ? 0x80010001u : 0x10002u; // DBG_EXCEPTION_NOT_HANDLED / DBG_CONTINUE
                int continueError = 0;
                try
                {
                    if (observedProcess != processId)
                    {
                        throw new InvalidOperationException("Native observation received an unowned process event.");
                    }
                    byte* payload = nativeEvent + unionOffset;
                    if (code is 3 or 6) // CREATE_PROCESS_DEBUG_EVENT / LOAD_DLL_DEBUG_EVENT
                    {
                        // Windows closes event process/thread handles; the debugger owns each event's image file.
                        new SafeFileHandle(Unsafe.ReadUnaligned<nint>(payload), ownsHandle: true).Dispose();
                    }
                    else if (code == 5) // EXIT_PROCESS_DEBUG_EVENT
                    {
                        phase?.Invoke("Native process exit received");
                        // CoreCLR can terminate on the first chance; retain the latest suppressed fault for that path.
                        if (Unsafe.ReadUnaligned<uint>(payload) != 0 && pendingFault is not null)
                        {
                            report(pendingFault);
                        }
                    }
                    else if (code == 1)
                    {
                        uint exception = Unsafe.ReadUnaligned<uint>(payload);
                        bool firstChance = Unsafe.ReadUnaligned<uint>(payload + (IntPtr.Size == 8 ? 152 : 80)) != 0;
                        if (initialBreakpoint && exception == 0x80000003 && firstChance)
                        {
                            initialBreakpoint = false;
                            disposition = 0x10002;
                            process.StandardInput.WriteLine("capture");
                            process.StandardInput.Close();
                            phase?.Invoke("Capture input released");
                        }
                        else if (exception == 0xc0000005)
                        {
                            string record = WindowsNativeFaultReport.Read(process, threadId, payload, firstChance);
                            if (!firstChance || firstChanceReports < 8)
                            {
                                if (firstChance)
                                {
                                    firstChanceReports++;
                                }
                                pendingFault = null;
                                report(record);
                            }
                            else
                            {
                                pendingFault = record;
                            }
                        }
                    }
                }
                finally
                {
                    if (ContinueDebugEvent(observedProcess, threadId, disposition) == 0)
                    {
                        continueError = Marshal.GetLastPInvokeError();
                    }
                }
                if (continueError != 0)
                {
                    throw new Win32Exception(continueError);
                }
                exited = code == 5;
                if (exited && phase is not null)
                {
                    // Observe kernel termination on this dedicated native-event thread, independently of pool callbacks.
                    using var handle = new WindowsProcessExitWaitHandle(process.SafeHandle);
                    if (WaitHandle.WaitAny([handle, cancellationToken.WaitHandle]) == 1)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    phase("Native wait observed kernel termination");
                }
            }
        }
        catch (Exception exception)
        {
            if (!exited && DebugActiveProcessStop(processId) == 0)
            {
                int detachError = Marshal.GetLastPInvokeError();
                if (!process.HasExited)
                {
                    exception.Data["NativeDebuggerDetachError"] = detachError;
                }
            }
            throw;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DebugActiveProcess(uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DebugActiveProcessStop(uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int DebugSetProcessKillOnExit(int killOnExit);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial int WaitForDebugEventEx(byte* nativeEvent, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int ContinueDebugEvent(uint processId, uint threadId, uint disposition);
}
