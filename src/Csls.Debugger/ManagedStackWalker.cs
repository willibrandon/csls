using Csls.Debugger.Interop;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Owns one iterative, cancellable CoreCLR stack walk on the debugger actor.
/// </summary>
internal sealed class ManagedStackWalker : IDisposable
{
    private const int EndOfStackHResult = 0x00131324;
    private const int MaximumWalkCount = 1024 * 1024;
    private nint _thread;
    private nint _thread3;
    private nint _walker;
    private int _walkCount;
    private bool _advance;
    private bool _ended;

    private ManagedStackWalker()
    {
    }

    /// <summary>
    /// Gets the index of the most recently returned managed frame.
    /// </summary>
    internal int FrameIndex { get; private set; } = -1;

    /// <summary>
    /// Gets the native interface references still owned by this walker.
    /// </summary>
    internal int OwnedInterfaceCount => (_thread == 0 ? 0 : 1) + (_thread3 == 0 ? 0 : 1) + (_walker == 0 ? 0 : 1);

    /// <summary>
    /// Opens an actor-owned walk for one stopped runtime thread.
    /// </summary>
    /// <param name="process">The borrowed ICorDebugProcess pointer.</param>
    /// <param name="threadId">The runtime thread identifier.</param>
    /// <param name="exitMonitor">The launched child's native wait owner, or null for attachment.</param>
    /// <param name="cancellationToken">Cancels between native thread and context operations.</param>
    /// <returns>The owned walk, which must be disposed on the actor.</returns>
    internal static ManagedStackWalker Open(nint process, int threadId, UnixChildExitMonitor? exitMonitor,
        CancellationToken cancellationToken)
    {
        var walk = new ManagedStackWalker();
        try
        {
            walk.Initialize(process, threadId, exitMonitor, cancellationToken);
            return walk;
        }
        catch
        {
            walk.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Transfers the next managed frame without retaining earlier frames or walking the remaining tail.
    /// </summary>
    /// <param name="frame">Receives an owned ICorDebugFrame pointer, or zero at the stack end.</param>
    /// <param name="cancellationToken">Cancels between native stack-walk calls.</param>
    /// <returns>True when the caller must consume or release the returned frame.</returns>
    internal unsafe bool TryTakeFrame(out nint frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_walker == 0, this);
        frame = 0;
        var walker = new ICorDebugStackWalkAbi(_walker);
        while (!_ended)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_advance)
            {
                int nextResult = walker.Next();
                if (nextResult == EndOfStackHResult)
                {
                    _ended = true;
                    return false;
                }

                CorDebugHResult.ThrowIfFailed(nextResult, "ICorDebugStackWalk.Next");
            }

            if (_walkCount == MaximumWalkCount)
            {
                throw new InvalidOperationException(
                    $"The target exceeds the stack-walk limit of {MaximumWalkCount} native positions.");
            }

            _walkCount++;
            _advance = true;
            cancellationToken.ThrowIfCancellationRequested();
            nint current = 0;
            try
            {
                nint* address = &current;
                int result = walker.GetFrame((nint)address);
                current = Volatile.Read(ref *address);
                CorDebugHResult.ThrowIfFailed(result, "ICorDebugStackWalk.GetFrame");
                cancellationToken.ThrowIfCancellationRequested();
                if (result == 0 && current != 0)
                {
                    FrameIndex++;
                    frame = current;
                    current = 0;
                    return true;
                }
            }
            finally
            {
                if (current != 0)
                {
                    _ = ComAbi.Release(current);
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Release(ref _walker);
        Release(ref _thread3);
        Release(ref _thread);
    }

    private unsafe void Initialize(nint process, int threadId, UnixChildExitMonitor? exitMonitor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        nint thread = 0;
        nint* threadAddress = &thread;
        int threadResult = new ICorDebugProcessAbi(process).GetThread(checked((uint)threadId), (nint)threadAddress);
        _thread = Volatile.Read(ref *threadAddress);
        CorDebugHResult.ThrowIfFailed(threadResult, "ICorDebugProcess.GetThread");
        if (_thread == 0)
        {
            throw new InvalidOperationException($"Managed thread {threadId} no longer exists.");
        }

        if (!ComAbi.TryQueryInterface(_thread, ICorDebugThread3Abi.InterfaceId, out _thread3))
        {
            throw new InvalidOperationException("The target runtime does not expose ICorDebugThread3 stack walking.");
        }

        nint walker = 0;
        nint* walkerAddress = &walker;
        int walkerResult = new ICorDebugThread3Abi(_thread3).CreateStackWalk((nint)walkerAddress);
        _walker = Volatile.Read(ref *walkerAddress);
        CorDebugHResult.ThrowIfFailed(walkerResult, "ICorDebugThread3.CreateStackWalk");
        if (_walker == 0)
        {
            throw new InvalidOperationException("ICorDebugThread3.CreateStackWalk returned no stack walker.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 &&
            !HasManagedArm64Context(process, threadId))
        {
            byte[] context = ReadNativeArm64Context(process, threadId, exitMonitor, cancellationToken);
            fixed (byte* contextAddress = context)
            {
                const int nonMatchingContext = unchecked((int)0x80131327);
                int result = new ICorDebugStackWalkAbi(_walker).SetContext(1,
                    checked((uint)context.Length), (nint)contextAddress);
                // CoreCLR preserves its managed walk when a native stop is outside the thread's stack bounds.
                if (result != nonMatchingContext)
                {
                    CorDebugHResult.ThrowIfFailed(result, "ICorDebugStackWalk.SetContext");
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static unsafe byte[] ReadNativeArm64Context(nint process, int threadId,
        UnixChildExitMonitor? exitMonitor, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            byte[] registers = exitMonitor is null
                ? LinuxThreadContext.ReadRegisters(threadId, cancellationToken)
                : exitMonitor.ReadRegisters(threadId, cancellationToken);
            return LinuxThreadContext.CreateArm64Context(registers);
        }

        if (OperatingSystem.IsMacOS())
        {
            uint processId = 0;
            uint* processIdAddress = &processId;
            CorDebugHResult.ThrowIfFailed(new ICorDebugProcessAbi(process).GetID((nint)processIdAddress),
                "ICorDebugProcess.GetID");
            return MacArm64ThreadContext.Read(checked((int)Volatile.Read(ref *processIdAddress)), threadId,
                cancellationToken);
        }

        throw new PlatformNotSupportedException("Native ARM64 context capture requires a Unix host.");
    }

    private static unsafe bool HasManagedArm64Context(nint process, int threadId)
    {
        // Managed stops retain the runtime's saved context, including breakpoint and exception dispatch.
        const int contextUnavailable = unchecked((int)0x80131C29);
        Span<byte> context = stackalloc byte[912];
        BinaryPrimitives.WriteUInt32LittleEndian(context, 0x00400003);
        int result;
        fixed (byte* address = context)
        {
            result = new ICorDebugProcessAbi(process).GetThreadContext(checked((uint)threadId),
                checked((uint)context.Length), (nint)address);
        }

        if (result == contextUnavailable)
        {
            return false;
        }

        CorDebugHResult.ThrowIfFailed(result, "ICorDebugProcess.GetThreadContext");
        return true;
    }

    private static void Release(ref nint pointer)
    {
        nint owned = Interlocked.Exchange(ref pointer, 0);
        if (owned != 0)
        {
            _ = ComAbi.Release(owned);
        }
    }
}
