using Csls.Debugger.Interop;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Copies runtime stack contexts through bounded buffers aligned to the native CONTEXT contract.
/// </summary>
internal static class ManagedStackContext
{
    private const int MaximumContextSize = 4096;
    private const int ContextAlignment = 16;

    /// <summary>
    /// Captures the current stack-walk registers into independently owned managed storage.
    /// </summary>
    /// <param name="pointer">The borrowed native stack walker.</param>
    /// <param name="cancellationToken">Cancels between native context reads.</param>
    /// <returns>The exact runtime context bytes.</returns>
    internal static unsafe byte[] Capture(nint pointer, CancellationToken cancellationToken)
    {
        uint flags = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => 0x00010007,
            Architecture.X64 => 0x0010000b,
            Architecture.Arm64 => 0x00400007,
            _ => throw new PlatformNotSupportedException("The current architecture has no supported runtime stack context.")
        };
        cancellationToken.ThrowIfCancellationRequested();
        var walker = new ICorDebugStackWalkAbi(pointer);
        uint size = 0;
        uint* sizeAddress = &size;
        CorDebugHResult.ThrowIfFailed(walker.GetContext(flags, 0, (nint)sizeAddress, 0), "ICorDebugStackWalk.GetContext");
        size = Volatile.Read(ref *sizeAddress);
        if (size is 0 or > MaximumContextSize)
        {
            throw new InvalidOperationException("The runtime stack context exceeds its supported buffer size.");
        }

        // Pinning a byte array preserves its address but does not establish native CONTEXT alignment.
        byte* storage = stackalloc byte[MaximumContextSize + ContextAlignment - 1];
        byte* address = (byte*)(((nuint)storage + ContextAlignment - 1) & ~(nuint)(ContextAlignment - 1));
        Span<byte> context = new(address, checked((int)size));
        context.Clear();
        cancellationToken.ThrowIfCancellationRequested();
        CorDebugHResult.ThrowIfFailed(walker.GetContext(flags, size, (nint)sizeAddress, (nint)address),
            "ICorDebugStackWalk.GetContext");
        if (Volatile.Read(ref *sizeAddress) != context.Length)
        {
            throw new InvalidOperationException("The runtime changed the stack context size during capture.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return context.ToArray();
    }

    /// <summary>
    /// Restores a saved context through aligned temporary storage without modifying the target thread.
    /// </summary>
    /// <param name="pointer">The borrowed native stack walker.</param>
    /// <param name="flags">Whether the context represents an active or unwound activation.</param>
    /// <param name="context">The exact runtime context bytes.</param>
    /// <returns>The runtime result of restoring the walk position.</returns>
    internal static unsafe int Set(nint pointer, int flags, ReadOnlySpan<byte> context)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(context.Length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(context.Length, MaximumContextSize);
        byte* storage = stackalloc byte[MaximumContextSize + ContextAlignment - 1];
        byte* address = (byte*)(((nuint)storage + ContextAlignment - 1) & ~(nuint)(ContextAlignment - 1));
        context.CopyTo(new Span<byte>(address, context.Length));
        return new ICorDebugStackWalkAbi(pointer).SetContext(flags, checked((uint)context.Length), (nint)address);
    }
}
