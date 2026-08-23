using Csls.Protocol;
using System.Globalization;

namespace Csls.Server;

/// <summary>
/// Maintains bounded encoded semantic-token results and computes minimal contiguous deltas.
/// </summary>
internal sealed class SemanticTokensCache
{
    private const int MaximumEntries = 128;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SemanticTokensCacheEntry> _entries =
        new(StringComparer.Ordinal);
    private readonly Queue<string> _entryOrder = new();
    private long _resultSequence;

    /// <summary>
    /// Stores and returns one complete document semantic-token result.
    /// </summary>
    /// <param name="documentUri">The result document.</param>
    /// <param name="workspaceGeneration">The immutable workspace generation.</param>
    /// <param name="data">The complete relative-encoded token data.</param>
    /// <returns>The complete result with a new opaque identifier.</returns>
    internal SemanticTokens StoreFull(
        DocumentUri documentUri,
        long workspaceGeneration,
        IReadOnlyList<int> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            (string resultId, IReadOnlyList<int> storedData) = Store(
                documentUri,
                workspaceGeneration,
                data);
            return new SemanticTokens
            {
                ResultId = resultId,
                Data = storedData
            };
        }
    }

    /// <summary>
    /// Stores current data and returns edits against a prior result or a complete fallback.
    /// </summary>
    /// <param name="documentUri">The result document.</param>
    /// <param name="workspaceGeneration">The immutable workspace generation.</param>
    /// <param name="previousResultId">The prior opaque result identifier.</param>
    /// <param name="data">The complete current relative-encoded token data.</param>
    /// <returns>A delta response or complete replacement when the prior result is unavailable.</returns>
    internal SemanticTokensDeltaResult StoreDelta(
        DocumentUri documentUri,
        long workspaceGeneration,
        string previousResultId,
        IReadOnlyList<int> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousResultId);
        ArgumentNullException.ThrowIfNull(data);
        lock (_gate)
        {
            _entries.TryGetValue(previousResultId, out SemanticTokensCacheEntry? previous);
            (string resultId, IReadOnlyList<int> storedData) = Store(
                documentUri,
                workspaceGeneration,
                data);
            if (previous is null || previous.DocumentUri != documentUri)
            {
                return new SemanticTokensDeltaResult
                {
                    ResultId = resultId,
                    Data = storedData
                };
            }

            return new SemanticTokensDeltaResult
            {
                ResultId = resultId,
                Edits = CreateEdits(previous.Data, storedData)
            };
        }
    }

    /// <summary>
    /// Releases every encoded result retained by this server session.
    /// </summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _entryOrder.Clear();
        }
    }

    private (string ResultId, IReadOnlyList<int> Data) Store(
        DocumentUri documentUri,
        long workspaceGeneration,
        IReadOnlyList<int> data)
    {
        int[] storedData = [.. data];
        string resultId = string.Create(
            CultureInfo.InvariantCulture,
            $"{workspaceGeneration:x}-{++_resultSequence:x}");
        _entries.Add(
            resultId,
            new SemanticTokensCacheEntry
            {
                DocumentUri = documentUri,
                Data = storedData
            });
        _entryOrder.Enqueue(resultId);
        while (_entryOrder.Count > MaximumEntries)
        {
            _entries.Remove(_entryOrder.Dequeue());
        }

        return (resultId, storedData);
    }

    private static IReadOnlyList<SemanticTokensEdit> CreateEdits(
        IReadOnlyList<int> previous,
        IReadOnlyList<int> current)
    {
        int prefixLength = 0;
        int commonLength = Math.Min(previous.Count, current.Count);
        while (prefixLength < commonLength &&
            previous[prefixLength] == current[prefixLength])
        {
            prefixLength++;
        }

        int suffixLength = 0;
        while (suffixLength < commonLength - prefixLength &&
            previous[previous.Count - suffixLength - 1] ==
                current[current.Count - suffixLength - 1])
        {
            suffixLength++;
        }

        int deleteCount = previous.Count - prefixLength - suffixLength;
        int insertCount = current.Count - prefixLength - suffixLength;
        if (deleteCount == 0 && insertCount == 0)
        {
            return [];
        }

        int[]? replacement = insertCount == 0
            ? null
            : [.. current.Skip(prefixLength).Take(insertCount)];
        return
        [
            new SemanticTokensEdit
            {
                Start = prefixLength,
                DeleteCount = deleteCount,
                Data = replacement
            }
        ];
    }
}
