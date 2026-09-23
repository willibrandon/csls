using Microsoft.Diagnostics.Runtime;

namespace Csls.Debugger.Dump;

/// <summary>
/// Resolves immutable heap storage without using approximate field types as constructed runtime types.
/// </summary>
/// <param name="runtime">The session-owned captured runtime.</param>
internal sealed class DumpCorDebugHeap(ClrRuntime runtime) : ICorDebugDumpHeap
{
    /// <inheritdoc />
    public ulong GetObjectSize(ulong address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClrObject instance = runtime.Heap.GetObject(address);
        if (!instance.IsValid)
        {
            throw new InvalidDataException("The captured reference does not identify a valid managed object.");
        }
        ulong size = instance.Size;
        cancellationToken.ThrowIfCancellationRequested();
        return size;
    }

    /// <inheritdoc />
    public ulong GetFieldAddress(ulong address, ulong moduleAddress, uint declaringType, uint field, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClrType type = runtime.Heap.GetObject(address).Type
            ?? throw new InvalidDataException("The captured object has no runtime type.");
        int count = 0;
        foreach (ClrInstanceField candidate in type.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 65536)
            {
                throw new InvalidDataException("The captured object exceeds the field limit of 65536.");
            }
            if (candidate.ContainingType.Module.ImageBase == moduleAddress &&
                unchecked((uint)candidate.ContainingType.MetadataToken) == declaringType &&
                unchecked((uint)candidate.Token) == field)
            {
                if (candidate.Offset < 0)
                {
                    throw new InvalidDataException("The captured field has no physical instance offset.");
                }
                return checked(address + (ulong)runtime.DataTarget.DataReader.PointerSize + (ulong)candidate.Offset);
            }
        }
        throw new CorDebugDumpStorageUnavailableException("The captured heap has no field storage matching the exact declaring identity.");
    }

    /// <inheritdoc />
    public CorDebugDumpArrayBounds GetArrayBounds(ulong address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClrArray array = runtime.Heap.GetObject(address).AsArray();
        int rank = array.Rank;
        if (rank is < 1 or > 32)
        {
            throw new InvalidDataException("The captured array rank is outside the CLR limit of 32 dimensions.");
        }
        int[] lengths = new int[rank];
        int[] bases = new int[rank];
        for (int dimension = 0; dimension < rank; dimension++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lengths[dimension] = array.GetLength(dimension);
            bases[dimension] = array.GetLowerBound(dimension);
        }
        return new CorDebugDumpArrayBounds(lengths, bases);
    }

    /// <inheritdoc />
    public (ulong LengthAddress, ulong CharacterAddress) GetStringStorage(ulong address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClrType type = runtime.Heap.GetObject(address).Type
            ?? throw new InvalidDataException("The captured string has no runtime type.");
        if (!type.IsString)
        {
            throw new InvalidDataException("The captured reference does not identify a runtime string.");
        }
        ClrInstanceField length = type.GetFieldByName("_stringLength")
            ?? throw new CorDebugDumpStorageUnavailableException("The captured string length field is unavailable.");
        ClrInstanceField character = type.GetFieldByName("_firstChar")
            ?? throw new CorDebugDumpStorageUnavailableException("The captured string character field is unavailable.");
        if (length.ElementType != ClrElementType.Int32 || character.ElementType != ClrElementType.Char ||
            length.Offset < 0 || character.Offset < 0)
        {
            throw new InvalidDataException("The captured string fields have invalid physical storage.");
        }
        ulong data = checked(address + (ulong)runtime.DataTarget.DataReader.PointerSize);
        cancellationToken.ThrowIfCancellationRequested();
        return (checked(data + (ulong)length.Offset), checked(data + (ulong)character.Offset));
    }
}
