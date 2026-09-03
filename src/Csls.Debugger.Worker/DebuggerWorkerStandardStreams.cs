using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Csls.Debugger.Worker;

/// <summary>
/// Owns stable worker streams that cannot be redirected by a Unix target launch.
/// </summary>
internal sealed partial class DebuggerWorkerStandardStreams : IAsyncDisposable
{
    private const int StandardInputDescriptor = 0;
    private const int StandardOutputDescriptor = 1;
    private const int StandardErrorDescriptor = 2;
    private const int SetFileDescriptorFlags = 2;
    private const int CloseOnExec = 1;
    private readonly SafeFileHandle? _inputHandle;
    private readonly SafeFileHandle? _outputHandle;
    private readonly SafeFileHandle? _errorHandle;
    private readonly Stream _input = Stream.Null;
    private readonly Stream _output = Stream.Null;
    private readonly Stream _errorStream = Stream.Null;
    private readonly StreamWriter? _error;

    private DebuggerWorkerStandardStreams(bool stabilizeInput)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _input = Console.OpenStandardInput();
                _output = Console.OpenStandardOutput();
                _errorStream = Console.OpenStandardError();
            }
            else
            {
                if (stabilizeInput)
                {
                    _inputHandle = DuplicateStandardDescriptor(StandardInputDescriptor);
                    _input = new FileStream(
                        _inputHandle,
                        FileAccess.Read,
                        bufferSize: 4096,
                        isAsync: false);
                }
                else
                {
                    _input = Console.OpenStandardInput();
                }

                _outputHandle = DuplicateStandardDescriptor(StandardOutputDescriptor);
                _errorHandle = DuplicateStandardDescriptor(StandardErrorDescriptor);
                _output = new FileStream(
                    _outputHandle,
                    FileAccess.Write,
                    bufferSize: 4096,
                    isAsync: false);
                _errorStream = new FileStream(
                    _errorHandle,
                    FileAccess.Write,
                    bufferSize: 4096,
                    isAsync: false);
            }

            _error = new StreamWriter(
                _errorStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true
            };
        }
        catch
        {
            _error?.Dispose();
            _input.Dispose();
            _output.Dispose();
            _errorStream.Dispose();
            _inputHandle?.Dispose();
            _outputHandle?.Dispose();
            _errorHandle?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gets the stable worker input stream.
    /// </summary>
    internal Stream Input => _input;

    /// <summary>
    /// Gets the stable worker output stream.
    /// </summary>
    internal Stream Output => _output;

    /// <summary>
    /// Gets the stable diagnostics writer.
    /// </summary>
    internal TextWriter Error => _error ?? throw new ObjectDisposedException(
        nameof(DebuggerWorkerStandardStreams));

    /// <summary>
    /// Opens stable copies of the worker's inherited standard streams.
    /// </summary>
    /// <param name="stabilizeInput">Whether standard input may be read while a target launch redirects process descriptors.</param>
    /// <returns>The owned standard-stream set.</returns>
    internal static DebuggerWorkerStandardStreams Open(bool stabilizeInput) =>
        new(stabilizeInput);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_error is not null)
        {
            await _error.DisposeAsync().ConfigureAwait(false);
        }

        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        await _errorStream.DisposeAsync().ConfigureAwait(false);
        _inputHandle?.Dispose();
        _outputHandle?.Dispose();
        _errorHandle?.Dispose();
    }

    private static SafeFileHandle DuplicateStandardDescriptor(int descriptor)
    {
        int duplicate = Duplicate(descriptor);
        if (duplicate < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"dup({descriptor})");
        }

        var handle = new SafeFileHandle(duplicate, ownsHandle: true);
        try
        {
            if (SetDescriptorFlags(duplicate, SetFileDescriptorFlags, CloseOnExec) < 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"fcntl({duplicate}, F_SETFD, FD_CLOEXEC)");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Duplicate(int descriptor);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetDescriptorFlags(int descriptor, int command, int flags);
}
