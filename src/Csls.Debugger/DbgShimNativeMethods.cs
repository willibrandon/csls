using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Declares NativeAOT-generated entry points from the official .NET debugger shim.
/// </summary>
internal static unsafe partial class DbgShimNativeMethods
{
    /// <summary>
    /// Creates a target process and optionally leaves it suspended for runtime activation.
    /// </summary>
    /// <param name="commandLine">The mutable command line interpreted by the operating system.</param>
    /// <param name="suspendProcess">One to suspend the target before managed startup.</param>
    /// <param name="environment">An optional native environment block.</param>
    /// <param name="currentDirectory">The target working directory.</param>
    /// <param name="processId">Receives the operating-system process identifier.</param>
    /// <param name="resumeHandle">Receives the platform resume handle.</param>
    /// <returns>An HRESULT describing the operation result.</returns>
    [LibraryImport(
        "dbgshim",
        EntryPoint = "CreateProcessForLaunch",
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int CreateProcessForLaunch(
        char* commandLine,
        int suspendProcess,
        nint environment,
        string? currentDirectory,
        out uint processId,
        out nint resumeHandle);

    /// <summary>
    /// Resumes a target created in the suspended state by the debugger shim.
    /// </summary>
    /// <param name="resumeHandle">The native launch resume handle.</param>
    /// <returns>An HRESULT describing the operation result.</returns>
    [LibraryImport("dbgshim", EntryPoint = "ResumeProcess")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int ResumeProcess(nint resumeHandle);

    /// <summary>
    /// Closes a launch resume handle created by the debugger shim.
    /// </summary>
    /// <param name="resumeHandle">The native launch resume handle.</param>
    /// <returns>An HRESULT describing the operation result.</returns>
    [LibraryImport("dbgshim", EntryPoint = "CloseResumeHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int CloseResumeHandle(nint resumeHandle);

    /// <summary>
    /// Registers a callback that receives the target's initialized ICorDebug instance.
    /// </summary>
    /// <param name="processId">The target operating-system process identifier.</param>
    /// <param name="callback">The unmanaged runtime-startup callback address.</param>
    /// <param name="parameter">An opaque value returned to the callback.</param>
    /// <param name="unregisterToken">Receives the callback registration token.</param>
    /// <returns>An HRESULT describing the operation result.</returns>
    [LibraryImport("dbgshim", EntryPoint = "RegisterForRuntimeStartup")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int RegisterForRuntimeStartup(
        uint processId,
        nint callback,
        nint parameter,
        out nint unregisterToken);

    /// <summary>
    /// Removes a runtime-startup callback registration.
    /// </summary>
    /// <param name="unregisterToken">The token returned by callback registration.</param>
    /// <returns>An HRESULT describing the operation result.</returns>
    [LibraryImport("dbgshim", EntryPoint = "UnregisterForRuntimeStartup")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    internal static partial int UnregisterForRuntimeStartup(nint unregisterToken);
}
