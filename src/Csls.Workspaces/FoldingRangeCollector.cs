using Csls.Protocol;

namespace Csls.Workspaces;

/// <summary>
/// Retains the earliest distinct folding ranges within one fixed result bound.
/// </summary>
internal sealed class FoldingRangeCollector
{
    private static readonly IComparer<FoldingRange> s_orderComparer =
        Comparer<FoldingRange>.Create(CompareRanges);
    private static readonly IComparer<FoldingRange> s_reverseOrderComparer =
        Comparer<FoldingRange>.Create(static (left, right) => CompareRanges(right, left));
    private readonly PriorityQueue<FoldingRange, FoldingRange> _ranges;
    private readonly HashSet<FoldingRange> _distinctRanges = [];
    private readonly int _maximumRangeCount;

    /// <summary>
    /// Creates a collector with one positive fixed result bound.
    /// </summary>
    /// <param name="maximumRangeCount">The maximum number of retained ranges.</param>
    internal FoldingRangeCollector(int maximumRangeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRangeCount);
        _maximumRangeCount = maximumRangeCount;
        _ranges = new PriorityQueue<FoldingRange, FoldingRange>(
            maximumRangeCount + 1,
            s_reverseOrderComparer);
    }

    /// <summary>
    /// Retains one range when it belongs within the earliest bounded result set.
    /// </summary>
    /// <param name="range">The folding range to consider.</param>
    internal void Add(FoldingRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!_distinctRanges.Add(range))
        {
            return;
        }

        _ranges.Enqueue(range, range);
        if (_ranges.Count > _maximumRangeCount)
        {
            FoldingRange removed = _ranges.Dequeue();
            _distinctRanges.Remove(removed);
        }
    }

    /// <summary>
    /// Returns the retained ranges in deterministic source order.
    /// </summary>
    /// <returns>The bounded ordered folding ranges.</returns>
    internal IReadOnlyList<FoldingRange> ToArray()
    {
        FoldingRange[] ranges = [.. _distinctRanges];
        Array.Sort(ranges, s_orderComparer);
        return ranges;
    }

    private static int CompareRanges(FoldingRange left, FoldingRange right)
    {
        int comparison = left.StartLine.CompareTo(right.StartLine);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Nullable.Compare(left.StartCharacter, right.StartCharacter);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.EndLine.CompareTo(left.EndLine);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Nullable.Compare(right.EndCharacter, left.EndCharacter);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Kind, right.Kind);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.CollapsedText, right.CollapsedText);
    }
}
