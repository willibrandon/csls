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
    private const int SetFileDescriptorFlags = 2;
    private const int CloseOnExec = 1;
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
            savedInput = DuplicateCloseOnExec(StandardInputDescriptor, "stdin");
            savedOutput = DuplicateCloseOnExec(StandardOutputDescriptor, "stdout");
            savedError = DuplicateCloseOnExec(StandardErrorDescriptor, "stderr");
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
        string workingDirectory) => WindowsProcessLauncher.Create(
            commandLine,
            environment,
            workingDirectory,
            _standardInput.ClientSafePipeHandle.DangerousGetHandle(),
            _standardOutput.ClientSafePipeHandle.DangerousGetHandle(),
            _standardError.ClientSafePipeHandle.DangerousGetHandle());

    private static void DuplicateTo(nint source, int destination)
    {
        int result = DuplicateDescriptor(checked((int)source), destination);
        ThrowIfUnixFailed(result, $"dup2({source}, {destination})");
    }

    private static int DuplicateCloseOnExec(int descriptor, string name)
    {
        int duplicate = Duplicate(descriptor);
        ThrowIfUnixFailed(duplicate, $"dup({name})");
        if (SetDescriptorFlags(duplicate, SetFileDescriptorFlags, CloseOnExec) >= 0)
        {
            return duplicate;
        }

        int error = Marshal.GetLastPInvokeError();
        _ = CloseDescriptor(duplicate);
        throw new Win32Exception(error, $"fcntl({name}, F_SETFD, FD_CLOEXEC)");
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

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetDescriptorFlags(int descriptor, int command, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int CloseDescriptor(int descriptor);

}
