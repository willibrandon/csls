using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Expands managed arrays with bounded paging and CLR index semantics.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private unsafe List<DebugVariableInfo> ExpandArray(
        nint array,
        DebugStopGeneration generation,
        int start,
        int count)
    {
        uint elementCount = 0;
        uint* elementCountAddress = &elementCount;
        var api = new ICorDebugArrayValueAbi(array);
        CorDebugHResult.ThrowIfFailed(
            api.GetCount((nint)elementCountAddress),
            "ICorDebugArrayValue.GetCount");
        elementCount = Volatile.Read(ref *elementCountAddress);
        if (elementCount > MaximumExpandableValueCount)
        {
            throw new InvalidOperationException(
                $"The array exceeds the debugger element limit of {MaximumExpandableValueCount}.");
        }

        uint rank = GetArrayRank(api);
        uint[] dimensions = GetArrayDimensions(api, rank);
        int[] bases = GetArrayBases(api, rank);
        int end = count == 0
            ? checked((int)elementCount)
            : Math.Min(checked((int)elementCount), checked(start + count));
        var result = new List<DebugVariableInfo>(Math.Max(0, end - start));
        for (int index = start; index < end; index++)
        {
            nint element = 0;
            nint* elementAddress = &element;
            CorDebugHResult.ThrowIfFailed(
                api.GetElementAtPosition(checked((uint)index), (nint)elementAddress),
                "ICorDebugArrayValue.GetElementAtPosition");
            element = Volatile.Read(ref *elementAddress);
            if (element == 0)
            {
                throw new InvalidOperationException(
                    "ICorDebugArrayValue.GetElementAtPosition returned no value.");
            }

            try
            {
                ManagedValueDisplay display = CorDebugValueFormatter.Format(element);
                result.Add(new DebugVariableInfo(
                    FormatArrayIndex(index, dimensions, bases),
                    display.Value,
                    display.Type,
                    RetainExpandableValue(element, generation)));
            }
            finally
            {
                _ = ComAbi.Release(element);
            }
        }

        return result;
    }

    private static unsafe uint GetArrayRank(ICorDebugArrayValueAbi array)
    {
        uint rank = 0;
        uint* rankAddress = &rank;
        CorDebugHResult.ThrowIfFailed(
            array.GetRank((nint)rankAddress),
            "ICorDebugArrayValue.GetRank");
        return Volatile.Read(ref *rankAddress);
    }

    private static unsafe uint[] GetArrayDimensions(ICorDebugArrayValueAbi array, uint rank)
    {
        uint[] dimensions = new uint[checked((int)rank)];
        fixed (uint* dimensionsAddress = dimensions)
        {
            CorDebugHResult.ThrowIfFailed(
                array.GetDimensions(rank, (nint)dimensionsAddress),
                "ICorDebugArrayValue.GetDimensions");
        }

        return dimensions;
    }

    private static unsafe int[] GetArrayBases(ICorDebugArrayValueAbi array, uint rank)
    {
        int hasBases = 0;
        int* hasBasesAddress = &hasBases;
        CorDebugHResult.ThrowIfFailed(
            array.HasBaseIndicies((nint)hasBasesAddress),
            "ICorDebugArrayValue.HasBaseIndicies");
        int[] bases = new int[checked((int)rank)];
        if (Volatile.Read(ref *hasBasesAddress) == 0)
        {
            return bases;
        }

        fixed (int* basesAddress = bases)
        {
            CorDebugHResult.ThrowIfFailed(
                array.GetBaseIndicies(rank, (nint)basesAddress),
                "ICorDebugArrayValue.GetBaseIndicies");
        }

        return bases;
    }

    private static string FormatArrayIndex(int position, uint[] dimensions, int[] bases)
    {
        int remainder = position;
        int[] indices = new int[dimensions.Length];
        for (int dimension = dimensions.Length - 1; dimension >= 0; dimension--)
        {
            int length = checked((int)dimensions[dimension]);
            indices[dimension] = bases[dimension] + (remainder % length);
            remainder /= length;
        }

        return $"[{string.Join(',', indices)}]";
    }
}
