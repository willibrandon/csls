using Csls.Debugger.Interop;
using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded immutable storage using public runtime layouts and exact constructed field types.
/// </summary>
internal sealed unsafe class CorDebugDumpStorageReader
{
    private readonly ICorDebugDumpSource _source;
    private readonly ICorDebugDumpHeap _heap;
    private readonly CorDebugDumpCallbacks _callbacks;
    private readonly Func<nint> _getProcess;
    private readonly ManagedCapturedTypeResolver _types;

    /// <summary>
    /// Creates request-scoped storage inspection over the session's captured process and heap services.
    /// </summary>
    internal CorDebugDumpStorageReader(ICorDebugDumpSource source, ICorDebugDumpHeap heap,
        CorDebugDumpCallbacks callbacks, Func<nint> getProcess, ManagedCapturedTypeResolver types)
    {
        _source = source;
        _heap = heap;
        _callbacks = callbacks;
        _getProcess = getProcess;
        _types = types;
    }

    /// <summary>
    /// Reacquires exact storage from a borrowed physical frame value without copying its heap payload.
    /// </summary>
    internal CorDebugDumpStorage FromValue(nint value)
    {
        nint value2 = ComAbi.QueryInterface(value, ICorDebugValue2Abi.InterfaceId);
        nint type = 0;
        try
        {
            CorDebugHResult.ThrowIfFailed(new ICorDebugValue2Abi(value2).GetExactType((nint)(&type)), "ICorDebugValue2.GetExactType");
            type = Volatile.Read(ref type);
            ulong address = 0;
            uint size = 0;
            var api = new ICorDebugValueAbi(value);
            CorDebugHResult.ThrowIfFailed(api.GetAddress((nint)(&address)), "ICorDebugValue.GetAddress");
            CorDebugHResult.ThrowIfFailed(api.GetSize((nint)(&size)), "ICorDebugValue.GetSize");
            CheckFiltered(Volatile.Read(ref address), Volatile.Read(ref size));
            if (ComAbi.TryQueryInterface(value, ICorDebugReferenceValueAbi.InterfaceId, out nint reference))
            {
                try
                {
                    ulong target = 0;
                    CorDebugHResult.ThrowIfFailed(new ICorDebugReferenceValueAbi(reference).GetValue((nint)(&target)),
                        "ICorDebugReferenceValue.GetValue");
                    return FromReference(type, Volatile.Read(ref target));
                }
                finally
                {
                    Release(reference);
                }
            }
            _ = ComAbi.AddRef(type);
            return new CorDebugDumpStorage(type, Volatile.Read(ref address), Volatile.Read(ref size), false);
        }
        finally
        {
            Release(type);
            Release(value2);
        }
    }

    /// <summary>
    /// Reacquires a request-owned native type for storage retained from this immutable captured process.
    /// </summary>
    internal CorDebugDumpStorage FromLocation(CorDebugDumpStorageLocation location)
    {
        _callbacks.Operation?.ThrowIfInterrupted();
        nint process5 = ComAbi.QueryInterface(_getProcess(), ICorDebugProcess5Abi.InterfaceId);
        nint type = 0;
        try
        {
            CorDebugHResult.ThrowIfFailed(new ICorDebugProcess5Abi(process5).GetTypeForTypeID(location.TypeId,
                (nint)(&type)), "ICorDebugProcess5.GetTypeForTypeID");
            type = Volatile.Read(ref type);
            var value = new CorDebugDumpStorage(type, location.Address, location.Size, location.HeapObject, location.TypeId);
            type = 0;
            return value;
        }
        finally
        {
            Release(type);
            Release(process5);
        }
    }

    /// <summary>
    /// Reads one physical field using exact declaring identity and explicit captured storage bounds.
    /// </summary>
    internal CorDebugDumpStorage ReadField(CorDebugDumpStorage parent, ManagedRuntimeTypeDeclaration declaration,
        FieldDefinitionHandle field, uint token, ulong moduleAddress)
    {
        ulong address;
        if (parent.HeapObject)
        {
            address = _heap.GetFieldAddress(parent.Address, moduleAddress, declaration.TypeToken, token, CancellationToken);
        }
        else
        {
            CorDebugTypeId id = parent.TypeId ?? GetTypeId(declaration.Type);
            CorDebugTypeLayout layout = GetTypeLayout(id);
            if (layout.NumFields > 65536)
            {
                throw new InvalidDataException("The captured value exceeds the field limit of 65536.");
            }
            var fields = new CorDebugFieldLayout[layout.NumFields];
            nint process5 = ComAbi.QueryInterface(_getProcess(), ICorDebugProcess5Abi.InterfaceId);
            try
            {
                uint fetched = 0;
                fixed (CorDebugFieldLayout* buffer = fields)
                {
                    CorDebugHResult.ThrowIfFailed(new ICorDebugProcess5Abi(process5).GetTypeFields(id, layout.NumFields,
                        (nint)buffer, (nint)(&fetched)), "ICorDebugProcess5.GetTypeFields");
                }
                if (Volatile.Read(ref fetched) != fields.Length)
                {
                    throw new InvalidDataException("The captured field layout is incomplete.");
                }
                int index = Array.FindIndex(fields, candidate => candidate.Token == token);
                if (index < 0)
                {
                    throw new InvalidDataException("The captured inline type has no matching field layout.");
                }
                address = checked(parent.Address + fields[index].Offset);
            }
            finally
            {
                Release(process5);
            }
        }
        ulong end = checked(parent.Address + parent.Size);
        if (address < parent.Address || address >= end)
        {
            throw new InvalidDataException("The captured field lies outside its containing storage.");
        }
        nint type = _types.Resolve(declaration, field);
        try
        {
            return FromStorage(type, address, end - address);
        }
        finally
        {
            Release(type);
        }
    }

    /// <summary>
    /// Gets the captured array layout from the exact opaque runtime type identity.
    /// </summary>
    internal CorDebugArrayLayout GetArrayLayout(CorDebugDumpStorage value)
    {
        nint process5 = ComAbi.QueryInterface(_getProcess(), ICorDebugProcess5Abi.InterfaceId);
        try
        {
            CorDebugArrayLayout layout = default;
            CorDebugTypeId id = value.TypeId
                ?? throw new InvalidDataException("The captured array has no heap type identity.");
            CorDebugHResult.ThrowIfFailed(new ICorDebugProcess5Abi(process5).GetArrayLayout(id,
                (nint)(&layout)), "ICorDebugProcess5.GetArrayLayout");
            if (layout.NumRanks is 0 or > 32 || layout.RankSize != 4 || layout.ElementSize == 0 ||
                layout.FirstElementOffset > 4096 || layout.CountOffset > layout.FirstElementOffset ||
                layout.FirstElementOffset - layout.CountOffset < sizeof(int))
            {
                throw new InvalidDataException("The captured array layout has invalid header bounds.");
            }
            return layout;
        }
        finally
        {
            Release(process5);
        }
    }

    /// <summary>
    /// Reads the complete bounded array header and verifies its total element extent.
    /// </summary>
    internal int GetArrayCount(CorDebugDumpStorage value, CorDebugArrayLayout layout)
    {
        byte[] header = new byte[layout.FirstElementOffset];
        Read(value, 0, header);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(checked((int)layout.CountOffset)));
        if (count < 0 || checked((ulong)count * layout.ElementSize + layout.FirstElementOffset) > value.Size)
        {
            throw new InvalidDataException("The captured array count exceeds its object storage.");
        }
        return count;
    }

    /// <summary>
    /// Gets dimensional bounds from the captured heap after validating the complete array header.
    /// </summary>
    internal CorDebugDumpArrayBounds GetArrayBounds(CorDebugDumpStorage value) =>
        _heap.GetArrayBounds(value.Address, CancellationToken);

    /// <summary>
    /// Resolves one flattened array element with its exact declared component type and physical slot extent.
    /// </summary>
    internal CorDebugDumpStorage GetElement(CorDebugDumpStorage value, int index)
    {
        CorDebugArrayLayout layout = GetArrayLayout(value);
        int count = GetArrayCount(value, layout);
        if ((uint)index >= (uint)count)
        {
            throw new InvalidDataException("The captured element is outside the array bounds.");
        }
        nint element = 0;
        try
        {
            CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(value.Type).GetFirstTypeParameter((nint)(&element)),
                "ICorDebugType.GetFirstTypeParameter");
            element = Volatile.Read(ref element);
            ulong address = checked(value.Address + layout.FirstElementOffset + (ulong)index * layout.ElementSize);
            return FromStorage(element, address, layout.ElementSize);
        }
        finally
        {
            Release(element);
        }
    }

    /// <summary>
    /// Reads and escapes a bounded captured string using its exact physical runtime fields.
    /// </summary>
    internal string ReadString(CorDebugDumpStorage value)
    {
        (ulong lengthAddress, ulong characterAddress) = _heap.GetStringStorage(value.Address, CancellationToken);
        if (lengthAddress < value.Address || characterAddress < value.Address)
        {
            throw new InvalidDataException("The captured string fields precede its object storage.");
        }
        Span<byte> count = stackalloc byte[sizeof(int)];
        Read(value, lengthAddress - value.Address, count);
        int length = BinaryPrimitives.ReadInt32LittleEndian(count);
        if (length < 0)
        {
            throw new InvalidDataException("The captured string length is negative.");
        }
        if (length > 1024 * 1024)
        {
            throw new InvalidOperationException("The target string exceeds the debugger limit of 1048576 characters.");
        }
        byte[] bytes = new byte[checked(length * sizeof(char))];
        Read(value, characterAddress - value.Address, bytes);
        return CorDebugValueFormatter.Quote(new string(MemoryMarshal.Cast<byte, char>(bytes)));
    }

    /// <summary>
    /// Reads exactly the requested unfiltered bytes and accounts for cancellation and inspection progress.
    /// </summary>
    internal void Read(CorDebugDumpStorage value, ulong offset, Span<byte> bytes)
    {
        _callbacks.Operation?.ThrowIfInterrupted();
        if (offset > value.Size || (ulong)bytes.Length > value.Size - offset)
        {
            throw new InvalidDataException("The requested captured bytes are outside their containing storage.");
        }
        if (bytes.IsEmpty)
        {
            return;
        }
        ulong address = checked(value.Address + offset);
        CheckFiltered(address, (ulong)bytes.Length);
        if (address == 0)
        {
            throw new CorDebugDumpStorageUnavailableException("The captured value has no physical memory storage.");
        }
        using var stream = new CorDebugDumpMemoryStream(_source, _callbacks, address, (ulong)bytes.Length);
        try
        {
            stream.ReadExactly(bytes);
        }
        catch (EndOfStreamException exception)
        {
            throw new CorDebugDumpStorageUnavailableException("The dump omitted bytes belonging to the captured value.", exception);
        }
    }

    private CorDebugDumpStorage FromStorage(nint type, ulong address, ulong size)
    {
        uint element = GetElementType(type);
        if (element is 0x0e or 0x12 or 0x14 or 0x1c or 0x1d)
        {
            _ = ComAbi.AddRef(type);
            using var slot = new CorDebugDumpStorage(type, address, size, false);
            Span<byte> bytes = stackalloc byte[IntPtr.Size];
            Read(slot, 0, bytes);
            return FromReference(type, bytes.Length == sizeof(ulong)
                ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        }
        _ = ComAbi.AddRef(type);
        return new CorDebugDumpStorage(type, address, size, false);
    }

    private CorDebugDumpStorage FromReference(nint declaredType, ulong address)
    {
        if (address == 0)
        {
            _ = ComAbi.AddRef(declaredType);
            return new CorDebugDumpStorage(declaredType, 0, 0, false);
        }
        nint process5 = ComAbi.QueryInterface(_getProcess(), ICorDebugProcess5Abi.InterfaceId);
        nint type = 0;
        try
        {
            CorDebugTypeId id = default;
            var api = new ICorDebugProcess5Abi(process5);
            CorDebugHResult.ThrowIfFailed(api.GetTypeID(address, (nint)(&id)), "ICorDebugProcess5.GetTypeID");
            CorDebugHResult.ThrowIfFailed(api.GetTypeForTypeID(id, (nint)(&type)), "ICorDebugProcess5.GetTypeForTypeID");
            type = Volatile.Read(ref type);
            ulong size = _heap.GetObjectSize(address, CancellationToken);
            uint kind = GetElementType(type);
            bool isReference = kind is 0x0e or 0x12 or 0x14 or 0x1c or 0x1d;
            if (!isReference)
            {
                CorDebugTypeLayout layout = GetTypeLayout(id);
                if (layout.BoxOffset >= size)
                {
                    throw new InvalidDataException("The captured boxed value offset exceeds its object size.");
                }
                address = checked(address + layout.BoxOffset);
                size -= layout.BoxOffset;
            }
            var value = new CorDebugDumpStorage(type, address, size, isReference, id);
            type = 0;
            return value;
        }
        finally
        {
            Release(type);
            Release(process5);
        }
    }

    private static CorDebugTypeId GetTypeId(nint type)
    {
        nint type2 = ComAbi.QueryInterface(type, ICorDebugType2Abi.InterfaceId);
        try
        {
            CorDebugTypeId id = default;
            CorDebugHResult.ThrowIfFailed(new ICorDebugType2Abi(type2).GetTypeID((nint)(&id)), "ICorDebugType2.GetTypeID");
            return id;
        }
        finally
        {
            Release(type2);
        }
    }

    private CorDebugTypeLayout GetTypeLayout(CorDebugTypeId id)
    {
        nint process5 = ComAbi.QueryInterface(_getProcess(), ICorDebugProcess5Abi.InterfaceId);
        try
        {
            CorDebugTypeLayout layout = default;
            CorDebugHResult.ThrowIfFailed(new ICorDebugProcess5Abi(process5).GetTypeLayout(id, (nint)(&layout)),
                "ICorDebugProcess5.GetTypeLayout");
            return layout;
        }
        finally
        {
            Release(process5);
        }
    }

    private static uint GetElementType(nint type)
    {
        uint element = 0;
        CorDebugHResult.ThrowIfFailed(new ICorDebugTypeAbi(type).GetType((nint)(&element)), "ICorDebugType.GetType");
        return Volatile.Read(ref element);
    }

    private void CheckFiltered(ulong address, ulong size)
    {
        if (_source.IsMemoryFiltered(address, size))
        {
            throw new CorDebugDumpStorageUnavailableException("storage was filtered when the dump was created.");
        }
    }

    private CancellationToken CancellationToken => _callbacks.Operation?.CancellationToken ?? CancellationToken.None;

    private static void Release(nint pointer)
    {
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}
