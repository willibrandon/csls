using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded captured array pages while retaining only logical storage paths between requests.
/// </summary>
/// <param name="callbacks">The captured-memory observations owned by the serialized virtual process.</param>
/// <param name="storage">The public runtime layout and captured-memory reader.</param>
/// <param name="types">The exact declared element type formatter.</param>
/// <param name="describe">Describes a borrowed element value within the current request.</param>
internal sealed class CorDebugDumpArrayReader(CorDebugDumpCallbacks callbacks, CorDebugDumpStorageReader storage, ManagedRuntimeTypeFormatter types,
    Func<CorDebugDumpStorage, string, CorDebugDumpValuePath, bool, DebugVariableInfo> describe)
{
    /// <summary>
    /// Reads a bounded captured array page with exact runtime indices and logical child paths.
    /// </summary>
    internal List<DebugVariableInfo> ReadPage(CorDebugDumpStorage array, CorDebugDumpValuePath path,
        int reference, int start, int count, CancellationToken cancellationToken)
    {
        CorDebugArrayLayout layout = storage.GetArrayLayout(array);
        int length = storage.GetArrayCount(array, layout);
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
        CorDebugDumpArrayBounds bounds = storage.GetArrayBounds(array);
        if (bounds.Lengths.Count != layout.NumRanks || bounds.LowerBounds.Count != layout.NumRanks)
        {
            throw new InvalidDataException("The captured array bounds disagree with its runtime rank.");
        }
        long product = 1;
        foreach (int dimension in bounds.Lengths)
        {
            if (dimension <= 0)
            {
                throw new InvalidDataException("A nonempty captured array has an invalid dimension length.");
            }
            product = checked(product * dimension);
        }
        if (product != length)
        {
            throw new InvalidDataException("The captured array dimensions disagree with its element count.");
        }
        string elementType = types.FormatArrayElementRuntimeType(array.Type);
        List<DebugVariableInfo> result = new(take);
        for (int index = start; index - start < take; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] indices = new int[bounds.Lengths.Count];
            int remainder = index;
            for (int dimension = indices.Length - 1; dimension >= 0; dimension--)
            {
                indices[dimension] = checked(bounds.LowerBounds[dimension] + remainder % bounds.Lengths[dimension]);
                remainder /= bounds.Lengths[dimension];
            }
            string name = $"[{string.Join(',', indices)}]";
            long missingMemory = callbacks.MissingMemoryReads;
            try
            {
                using CorDebugDumpStorage element = storage.GetElement(array, index);
                result.Add(describe(element, name, path with
                {
                    ParentId = reference,
                    ElementIndex = index,
                    Field = null,
                    Depth = checked(path.Depth + 1)
                }, true));
            }
            catch (InvalidOperationException exception) when (CorDebugDumpValueReader.IsUnavailable(exception) ||
                callbacks.IsMissingMemoryFailure(exception, missingMemory))
            {
                result.Add(CorDebugDumpValueReader.Unavailable(name, exception, indexed: true) with { Type = elementType });
            }
            catch (CorDebugDumpStorageUnavailableException exception)
            {
                result.Add(CorDebugDumpValueReader.Unavailable(name, exception, indexed: true) with { Type = elementType });
            }
        }
        return result;
    }
}
