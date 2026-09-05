using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Worker;

/// <summary>
/// Reads a stable Unix descriptor with a cancellation pipe that interrupts an idle native wait.
/// </summary>
internal sealed partial class UnixDebuggerInputStream : Stream
{
    private const int Interrupted = 4;
    private const short Readable = 1;
    private const int SetFileDescriptorFlags = 2;
    private const int CloseOnExec = 1;
    private readonly SafeFileHandle _input;
    private readonly SafeFileHandle _wakeRead;
    private readonly SafeFileHandle _wakeWrite;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _wakePending;
    private int _disposed;

    /// <summary>
    /// Borrows a stable input descriptor whose owner must outlive this stream.
    /// </summary>
    /// <param name="input">The worker's noninheritable duplicate of standard input.</param>
    internal UnixDebuggerInputStream(SafeFileHandle input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        (_wakeRead, _wakeWrite) = CreateWakePipe();
    }

    /// <inheritdoc />
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ObjectDisposedException.ThrowIf(!CanRead, this);
        _readGate.Wait(_lifetime.Token);
        try
        {
            return ReadCore(buffer.AsSpan(offset, count), _lifetime.Token);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(!CanRead, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadCore(buffer.Span, linked.Token), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <inheritdoc />
    public override void Flush() { }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _readGate.Wait();
            _wakeRead.Dispose();
            _wakeWrite.Dispose();
            _lifetime.Dispose();
            _readGate.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ReadCore(Span<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        try
        {
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((UnixDebuggerInputStream)state!).WakeReader(), this);
            UnixInputPollDescriptor[] descriptors =
            [
                new() { _descriptor = (int)_input.DangerousGetHandle(), _events = Readable, _returnedEvents = 0 },
                new() { _descriptor = (int)_wakeRead.DangerousGetHandle(), _events = Readable, _returnedEvents = 0 }
            ];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int ready = OperatingSystem.IsMacOS()
                    ? PollMacOS(ref descriptors[0], 2, -1)
                    : PollLinux(ref descriptors[0], new CULong(2), -1);
                if (ready < 0)
                {
                    ThrowUnlessInterrupted("poll");
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (descriptors[0]._returnedEvents != 0)
                {
                    nint count = ReadDescriptor(_input, ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);
                    if (count >= 0)
                    {
                        return checked((int)count);
                    }

                    ThrowUnlessInterrupted("read");
                }
            }
        }
        finally
        {
            // The inner using scope settles the callback before draining its signal.
            if (Interlocked.Exchange(ref _wakePending, 0) != 0)
            {
                byte signal = 0;
                while (ReadDescriptor(_wakeRead, ref signal, 1) < 0)
                {
                    ThrowUnlessInterrupted("read cancellation pipe");
                }
            }
        }
    }

    private void WakeReader()
    {
        byte signal = 1;
        while (WriteDescriptor(_wakeWrite, ref signal, 1) < 0)
        {
            ThrowUnlessInterrupted("write cancellation pipe");
        }

        Volatile.Write(ref _wakePending, 1);
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreateWakePipe()
    {
        int[] descriptors = new int[2];
        if (CreatePipe(ref descriptors[0]) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "pipe");
        }

        var read = new SafeFileHandle(descriptors[0], ownsHandle: true);
        try
        {
            var write = new SafeFileHandle(descriptors[1], ownsHandle: true);
            try
            {
                if (SetDescriptorFlags(read, SetFileDescriptorFlags, CloseOnExec) != 0 ||
                    SetDescriptorFlags(write, SetFileDescriptorFlags, CloseOnExec) != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "fcntl(FD_CLOEXEC)");
                }

                return (read, write);
            }
            catch
            {
                write.Dispose();
                throw;
            }
        }
        catch
        {
            read.Dispose();
            throw;
        }
    }

    private static void ThrowUnlessInterrupted(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        if (error != Interrupted)
        {
            throw new IOException($"Unable to {operation} on the debugger input stream.", new Win32Exception(error));
        }
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int PollLinux(ref UnixInputPollDescriptor descriptors, CULong count, int timeout);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int PollMacOS(ref UnixInputPollDescriptor descriptors, uint count, int timeout);

    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int CreatePipe(ref int descriptors);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int SetDescriptorFlags(SafeFileHandle descriptor, int command, int flags);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint ReadDescriptor(SafeFileHandle descriptor, ref byte buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint WriteDescriptor(SafeFileHandle descriptor, ref byte buffer, nuint length);
}
