using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Owns a Windows process-thread attribute list backed by native memory.
/// </summary>
internal sealed unsafe partial class WindowsProcessThreadAttributeList : IDisposable
{
    private const nuint HandleListAttribute = 0x00020002;
    private nint _buffer;

    private WindowsProcessThreadAttributeList(nint buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Gets the initialized native process-thread attribute-list pointer.
    /// </summary>
    internal nint Pointer => _buffer;

    /// <summary>
    /// Creates an attribute list with capacity for one attribute.
    /// </summary>
    /// <returns>An owned initialized attribute list.</returns>
    internal static WindowsProcessThreadAttributeList Create()
    {
        nuint byteCount = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref byteCount);
        if (byteCount == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        void* allocation = NativeMemory.Alloc(byteCount);
        if (allocation is null)
        {
            throw new InvalidOperationException(
                "The Windows process attribute-list allocation failed.");
        }

        nint buffer = (nint)allocation;
        try
        {
            if (InitializeProcThreadAttributeList(buffer, 1, 0, ref byteCount) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new WindowsProcessThreadAttributeList(buffer);
        }
        catch
        {
            NativeMemory.Free(allocation);
            throw;
        }
    }

    /// <summary>
    /// Restricts child inheritance to the supplied inheritable handles.
    /// </summary>
    /// <param name="handles">The exact standard-stream handles the child may inherit.</param>
    internal void SetInheritedHandles(ReadOnlySpan<nint> handles)
    {
        ObjectDisposedException.ThrowIf(_buffer == 0, this);
        ArgumentOutOfRangeException.ThrowIfZero(handles.Length);
        fixed (nint* handlesPointer = handles)
        {
            if (UpdateProcThreadAttribute(
                _buffer,
                0,
                HandleListAttribute,
                (nint)handlesPointer,
                checked((nuint)handles.Length * (nuint)sizeof(nint)),
                0,
                0) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        nint buffer = Interlocked.Exchange(ref _buffer, 0);
        if (buffer != 0)
        {
            DeleteProcThreadAttributeList(buffer);
            NativeMemory.Free((void*)buffer);
        }
    }

    [LibraryImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int InitializeProcThreadAttributeList(
        nint attributeList,
        uint attributeCount,
        uint flags,
        ref nuint byteCount);

    [LibraryImport("kernel32", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [LibraryImport("kernel32")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);
}
