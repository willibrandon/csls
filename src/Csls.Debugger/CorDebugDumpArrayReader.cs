using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded captured array pages while retaining only logical storage paths between requests.
/// </summary>
/// <param name="values">The owning session's logical expansion paths.</param>
/// <param name="source">The captured memory and its storage provenance.</param>
internal sealed unsafe class CorDebugDumpArrayReader(CorDebugDumpValues values, ICorDebugDumpSource source)
{
    /// <summary>
    /// Formats a captured value and records an expansion path when it contains an array.
    /// </summary>
    internal DebugVariableInfo Describe(nint value, string name, CorDebugDumpValuePath path, bool indexed = false)
    {
        ulong address = 0;
        uint size = 0;
        var api = new ICorDebugValueAbi(value);
        CorDebugHResult.ThrowIfFailed(api.GetAddress((nint)(&address)), "ICorDebugValue.GetAddress");
        CorDebugHResult.ThrowIfFailed(api.GetSize((nint)(&size)), "ICorDebugValue.GetSize");
        if (source.IsMemoryFiltered(Volatile.Read(ref address), Volatile.Read(ref size)))
        {
            return new DebugVariableInfo(name, "Captured value unavailable: storage was filtered when the dump was created.",
                string.Empty, 0, null, null, DebugVariablePresentationKind.Unavailable, IsIndexed: indexed);
        }
        ManagedValueDisplay display = CorDebugValueFormatter.Format(value);
        nint array = GetArray(value);
        try
        {
            if (array == 0)
            {
                return new DebugVariableInfo(name, display.Value, display.Type, 0, null, null, IsIndexed: indexed);
            }

            int length = GetCount(array);
            int reference = length == 0 ? 0 : values.Retain(path);
            return new DebugVariableInfo(name, $"{{Length = {length}}}", display.Type, reference,
                null, null, NamedVariables: 0, IndexedVariables: length, IsIndexed: indexed);
        }
        finally
        {
            Release(array);
        }
    }

    /// <summary>
    /// Reacquires one logical array path from its captured root and returns the selected indexed page.
    /// </summary>
    internal IReadOnlyList<DebugVariableInfo> Read(nint frame, CorDebugDumpValuePath path,
        int reference, int start, int count, DebugVariableFilter filter, CancellationToken cancellationToken)
    {
        List<int> selections = [];
        CorDebugDumpValuePath root = path;
        while (root.ParentId != 0)
        {
            selections.Add(root.ElementIndex);
            root = values.Get(root.ParentId);
        }

        nint value = 0;
        try
        {
            var api = new ICorDebugILFrameAbi(frame);
            int result = root.Arguments ? api.GetArgument((uint)root.Slot, (nint)(&value))
                : api.GetLocalVariable((uint)root.Slot, (nint)(&value));
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugILFrame.GetValue");
            value = Volatile.Read(ref value);
            for (int index = selections.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint parent = GetArray(value);
                try
                {
                    nint child = GetElement(parent, selections[index]);
                    Release(value);
                    value = child;
                }
                finally
                {
                    Release(parent);
                }
            }

            nint array = GetArray(value);
            try
            {
                if (array == 0)
                {
                    throw new InvalidOperationException("The captured storage no longer resolves to an array.");
                }

                return filter == DebugVariableFilter.Named ? []
                    : ReadPage(array, path, reference, start, count, cancellationToken);
            }
            finally
            {
                Release(array);
            }
        }
        finally
        {
            Release(value);
        }
    }

    private List<DebugVariableInfo> ReadPage(nint array, CorDebugDumpValuePath path,
        int reference, int start, int count, CancellationToken cancellationToken)
    {
        int length = GetCount(array);
        int take = start >= length ? 0 : length - start;
        take = count == 0 ? take : Math.Min(count, take);
        if (take > 4096)
        {
            throw new InvalidDataException("The requested captured array page exceeds the response limit of 4096 values.");
        }

        if (take == 0)
        {
            return [];
        }

        var api = new ICorDebugArrayValueAbi(array);
        uint rank = 0;
        CorDebugHResult.ThrowIfFailed(api.GetRank((nint)(&rank)), "ICorDebugArrayValue.GetRank");
        rank = Volatile.Read(ref rank);
        if (rank is 0 or > 32)
        {
            throw new InvalidDataException("The captured array rank is outside the CLR limit of 32 dimensions.");
        }

        uint[] dimensions = new uint[rank];
        int[] bases = new int[rank];
        fixed (uint* dimensionAddress = dimensions)
        fixed (int* baseAddress = bases)
        {
            CorDebugHResult.ThrowIfFailed(api.GetDimensions(rank, (nint)dimensionAddress), "ICorDebugArrayValue.GetDimensions");
            int hasBases = 0;
            CorDebugHResult.ThrowIfFailed(api.HasBaseIndicies((nint)(&hasBases)), "ICorDebugArrayValue.HasBaseIndicies");
            if (Volatile.Read(ref hasBases) != 0)
            {
                CorDebugHResult.ThrowIfFailed(api.GetBaseIndicies(rank, (nint)baseAddress), "ICorDebugArrayValue.GetBaseIndicies");
            }
        }

        long product = 1;
        foreach (uint dimension in dimensions)
        {
            product = checked(product * dimension);
        }
        if (product != length)
        {
            throw new InvalidDataException("The captured array dimensions disagree with its element count.");
        }

        List<DebugVariableInfo> result = new(take);
        for (int index = start; index - start < take; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint element = 0;
            int[] indices = new int[rank];
            int remainder = index;
            for (int dimension = dimensions.Length - 1; dimension >= 0; dimension--)
            {
                indices[dimension] = checked(bases[dimension] + remainder % (int)dimensions[dimension]);
                remainder /= (int)dimensions[dimension];
            }
            string name = $"[{string.Join(',', indices)}]";
            try
            {
                element = GetElement(array, index);
                result.Add(Describe(element, name, path with
                {
                    ParentId = reference,
                    ElementIndex = index,
                    Depth = checked(path.Depth + 1)
                }, indexed: true));
            }
            catch (InvalidOperationException exception) when (IsUnavailable(exception))
            {
                result.Add(Unavailable(name, exception, indexed: true));
            }
            finally
            {
                Release(element);
            }
        }
        return result;
    }

    /// <summary>
    /// Identifies runtime errors caused by missing captured storage or optimized values.
    /// </summary>
    internal static bool IsUnavailable(InvalidOperationException exception) => exception.InnerException?.HResult is
        unchecked((int)0x80131304) or unchecked((int)0x80131305) or unchecked((int)0x80131c49);

    /// <summary>
    /// Projects unavailable captured storage without inventing a value or expansion handle.
    /// </summary>
    internal static DebugVariableInfo Unavailable(string name, Exception exception, bool indexed = false) =>
        new(name, $"Captured value unavailable: {exception.Message}", string.Empty, 0, null, null,
            DebugVariablePresentationKind.Unavailable, IsIndexed: indexed);

    private static nint GetArray(nint value, int depth = 0)
    {
        if (value == 0 || depth > 8)
        {
            return 0;
        }
        if (ComAbi.TryQueryInterface(value, ICorDebugArrayValueAbi.InterfaceId, out nint array))
        {
            return array;
        }
        if (!ComAbi.TryQueryInterface(value, ICorDebugReferenceValueAbi.InterfaceId, out nint reference))
        {
            return 0;
        }
        nint target = 0;
        try
        {
            int isNull = 0;
            var api = new ICorDebugReferenceValueAbi(reference);
            CorDebugHResult.ThrowIfFailed(api.IsNull((nint)(&isNull)), "ICorDebugReferenceValue.IsNull");
            if (Volatile.Read(ref isNull) != 0)
            {
                return 0;
            }
            CorDebugHResult.ThrowIfFailed(api.Dereference((nint)(&target)), "ICorDebugReferenceValue.Dereference");
            return GetArray(Volatile.Read(ref target), depth + 1);
        }
        finally
        {
            Release(target);
            Release(reference);
        }
    }

    private static int GetCount(nint array)
    {
        uint count = 0;
        CorDebugHResult.ThrowIfFailed(new ICorDebugArrayValueAbi(array).GetCount((nint)(&count)), "ICorDebugArrayValue.GetCount");
        return checked((int)Volatile.Read(ref count));
    }

    private static nint GetElement(nint array, int index)
    {
        if (array == 0)
        {
            throw new InvalidOperationException("The captured parent value is not an array.");
        }
        nint value = 0;
        int result = new ICorDebugArrayValueAbi(array).GetElementAtPosition((uint)index, (nint)(&value));
        value = Volatile.Read(ref value);
        if (result < 0)
        {
            Release(value);
            CorDebugHResult.ThrowIfFailed(result, "ICorDebugArrayValue.GetElementAtPosition");
        }
        return value != 0 ? value : throw new InvalidOperationException("The captured array element has no value interface.");
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            _ = ComAbi.Release(value);
        }
    }
}
