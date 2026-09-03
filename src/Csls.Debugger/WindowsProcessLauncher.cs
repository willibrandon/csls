using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Creates a suspended Windows target with Unicode state and isolated standard handles.
/// </summary>
internal static unsafe partial class WindowsProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint UseStandardHandles = 0x00000100;

    /// <summary>
    /// Creates a suspended target and transfers ownership of its initial-thread handle.
    /// </summary>
    /// <param name="commandLine">The complete mutable target command line.</param>
    /// <param name="environment">The complete double-null-terminated UTF-16 environment block.</param>
    /// <param name="workingDirectory">The absolute target working directory.</param>
    /// <param name="standardInput">The inheritable child standard-input handle.</param>
    /// <param name="standardOutput">The inheritable child standard-output handle.</param>
    /// <param name="standardError">The inheritable child standard-error handle.</param>
    /// <returns>The process identifier and owned suspended initial-thread handle.</returns>
    internal static (uint ProcessId, nint ResumeHandle) Create(
        string commandLine,
        nint environment,
        string workingDirectory,
        nint standardInput,
        nint standardOutput,
        nint standardError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfZero(environment);
        nint[] inheritedHandles = [standardInput, standardOutput, standardError];
        if (inheritedHandles.Any(static handle => handle is 0 or -1))
        {
            throw new ArgumentException(
                "Every Windows target standard-stream handle must be valid.",
                nameof(standardInput));
        }

        using var attributes = WindowsProcessThreadAttributeList.Create();
        attributes.SetInheritedHandles(inheritedHandles);
        var startupInfo = new WindowsStartupInfoEx
        {
            _startupInfo = new WindowsStartupInfo
            {
                _size = checked((uint)sizeof(WindowsStartupInfoEx)),
                _flags = UseStandardHandles,
                _standardInput = standardInput,
                _standardOutput = standardOutput,
                _standardError = standardError
            },
            _attributeList = attributes.Pointer
        };
        char[] mutableCommandLine = [.. commandLine, '\0'];
        WindowsProcessInformation processInformation;
        fixed (char* commandLinePointer = mutableCommandLine)
        {
            int created = CreateProcess(
                0,
                commandLinePointer,
                0,
                0,
                inheritHandles: 1,
                CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                environment,
                workingDirectory,
                ref startupInfo,
                out processInformation);
            if (created == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        using var processHandle = new SafeWaitHandle(
            processInformation._processHandle,
            ownsHandle: true);
        using var threadHandle = new SafeWaitHandle(
            processInformation._threadHandle,
            ownsHandle: true);
        if (processInformation._processId == 0 || processHandle.IsInvalid || threadHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "CreateProcessW succeeded without returning complete target ownership.");
        }

        nint resumeHandle = threadHandle.DangerousGetHandle();
        threadHandle.SetHandleAsInvalid();
        return (processInformation._processId, resumeHandle);
    }

    [LibraryImport(
        "kernel32",
        EntryPoint = "CreateProcessW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CreateProcess(
        nint applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        int inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref WindowsStartupInfoEx startupInfo,
        out WindowsProcessInformation processInformation);
}
