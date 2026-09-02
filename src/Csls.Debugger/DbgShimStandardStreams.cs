using Csls.Debugger.Interop;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Owns redirected target streams and atomically lends their child handles to dbgshim launch.
/// </summary>
internal sealed partial class DbgShimStandardStreams : IAsyncDisposable
{
    private const int StandardInputDescriptor = 0;
    private const int StandardOutputDescriptor = 1;
    private const int StandardErrorDescriptor = 2;
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;
    private static readonly SemaphoreSlim s_launchGate = new(1, 1);
    private readonly AnonymousPipeServerStream _standardInput =
        new(PipeDirection.Out, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _standardOutput =
        new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _standardError =
        new(PipeDirection.In, HandleInheritability.Inheritable);
    private int _childHandlesClosed;

    /// <summary>
    /// Gets the parent stream that writes target standard input.
    /// </summary>
    internal Stream StandardInput => _standardInput;

    /// <summary>
    /// Gets the parent stream that reads target standard output.
    /// </summary>
    internal Stream StandardOutput => _standardOutput;

    /// <summary>
    /// Gets the parent stream that reads target standard error.
    /// </summary>
    internal Stream StandardError => _standardError;

    /// <summary>
    /// Creates a suspended target while its inherited standard handles point only at these pipes.
    /// </summary>
    /// <param name="commandLine">The mutable-command-line content consumed by dbgshim.</param>
    /// <param name="environment">The complete platform environment block.</param>
    /// <param name="workingDirectory">The absolute target working directory.</param>
    /// <param name="cancellationToken">Cancels waiting for the process-wide launch gate.</param>
    /// <returns>The operating-system process identifier and owned resume handle.</returns>
    internal async Task<(uint ProcessId, nint ResumeHandle)> CreateSuspendedAsync(
        string commandLine,
        nint environment,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        await s_launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateSuspendedWindows(commandLine, environment, workingDirectory)
                : CreateSuspendedUnix(commandLine, environment, workingDirectory);
        }
        finally
        {
            CloseChildHandles();
            _ = s_launchGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CloseChildHandles();
        await _standardInput.DisposeAsync().ConfigureAwait(false);
        await _standardOutput.DisposeAsync().ConfigureAwait(false);
        await _standardError.DisposeAsync().ConfigureAwait(false);
    }

    private static unsafe (uint ProcessId, nint ResumeHandle) InvokeCreateProcess(
        string commandLine,
        nint environment,
        string workingDirectory)
    {
        char[] mutableCommandLine = [.. commandLine, '\0'];
        uint processId;
        nint resumeHandle;
        fixed (char* commandLinePointer = mutableCommandLine)
        {
            int result = DbgShimNativeMethods.CreateProcessForLaunch(
                commandLinePointer,
                suspendProcess: 1,
                environment,
                workingDirectory,
                out processId,
                out resumeHandle);
            CorDebugHResult.ThrowIfFailed(result, "CreateProcessForLaunch");
        }

        return (processId, resumeHandle);
    }

    private (uint ProcessId, nint ResumeHandle) CreateSuspendedUnix(
        string commandLine,
        nint environment,
        string workingDirectory)
    {
        int savedInput = -1;
        int savedOutput = -1;
        int savedError = -1;
        try
        {
            savedInput = Duplicate(StandardInputDescriptor);
            ThrowIfUnixFailed(savedInput, "dup(stdin)");
            savedOutput = Duplicate(StandardOutputDescriptor);
            ThrowIfUnixFailed(savedOutput, "dup(stdout)");
            savedError = Duplicate(StandardErrorDescriptor);
            ThrowIfUnixFailed(savedError, "dup(stderr)");
            DuplicateTo(_standardInput.ClientSafePipeHandle.DangerousGetHandle(), StandardInputDescriptor);
            DuplicateTo(_standardOutput.ClientSafePipeHandle.DangerousGetHandle(), StandardOutputDescriptor);
            DuplicateTo(_standardError.ClientSafePipeHandle.DangerousGetHandle(), StandardErrorDescriptor);
            return InvokeCreateProcess(commandLine, environment, workingDirectory);
        }
        finally
        {
            try
            {
                RestoreUnixDescriptor(savedInput, StandardInputDescriptor);
            }
            finally
            {
                try
                {
                    RestoreUnixDescriptor(savedOutput, StandardOutputDescriptor);
                }
                finally
                {
                    RestoreUnixDescriptor(savedError, StandardErrorDescriptor);
                }
            }
        }
    }

    private (uint ProcessId, nint ResumeHandle) CreateSuspendedWindows(
        string commandLine,
        nint environment,
        string workingDirectory)
    {
        nint savedInput = GetStandardHandle(StandardInputHandle);
        nint savedOutput = GetStandardHandle(StandardOutputHandle);
        nint savedError = GetStandardHandle(StandardErrorHandle);
        try
        {
            SetStandardHandle(StandardInputHandle, _standardInput.ClientSafePipeHandle.DangerousGetHandle());
            SetStandardHandle(StandardOutputHandle, _standardOutput.ClientSafePipeHandle.DangerousGetHandle());
            SetStandardHandle(StandardErrorHandle, _standardError.ClientSafePipeHandle.DangerousGetHandle());
            return InvokeCreateProcess(commandLine, environment, workingDirectory);
        }
        finally
        {
            try
            {
                SetStandardHandle(StandardInputHandle, savedInput);
            }
            finally
            {
                try
                {
                    SetStandardHandle(StandardOutputHandle, savedOutput);
                }
                finally
                {
                    SetStandardHandle(StandardErrorHandle, savedError);
                }
            }
        }
    }

    private static void DuplicateTo(nint source, int destination)
    {
        int result = DuplicateDescriptor(checked((int)source), destination);
        ThrowIfUnixFailed(result, $"dup2({source}, {destination})");
    }

    private static void RestoreUnixDescriptor(int saved, int destination)
    {
        if (saved < 0)
        {
            return;
        }

        try
        {
            int result = DuplicateDescriptor(saved, destination);
            ThrowIfUnixFailed(result, $"dup2({saved}, {destination})");
        }
        finally
        {
            _ = CloseDescriptor(saved);
        }
    }

    private static void ThrowIfUnixFailed(int result, string operation)
    {
        if (result < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), operation);
        }
    }

    private static void SetStandardHandle(int standardHandle, nint value)
    {
        if (!SetStandardHandleNative(standardHandle, value))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private void CloseChildHandles()
    {
        if (Interlocked.Exchange(ref _childHandlesClosed, 1) != 0)
        {
            return;
        }

        _standardInput.DisposeLocalCopyOfClientHandle();
        _standardOutput.DisposeLocalCopyOfClientHandle();
        _standardError.DisposeLocalCopyOfClientHandle();
    }

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Duplicate(int descriptor);

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int DuplicateDescriptor(int source, int destination);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int CloseDescriptor(int descriptor);

    [LibraryImport("kernel32", EntryPoint = "GetStdHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStandardHandle(int standardHandle);

    [LibraryImport("kernel32", EntryPoint = "SetStdHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStandardHandleNative(int standardHandle, nint value);
}
