namespace Csls.Debugger;

/// <summary>
/// Resolves source breakpoint requests to managed-symbol sequence points.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private Dictionary<int, SourceBreakpointLocation> ResolveLocations(
        IReadOnlyList<ManagedSequencePoint> sequencePoints,
        IReadOnlyList<SourceBreakpointDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, SourceBreakpointLocation>();
        IGrouping<string, SourceBreakpointDefinition>[] requestedDocuments =
            [.. definitions.GroupBy(static definition => definition.SourcePath, PathComparer)];
        Dictionary<string, List<ManagedSequencePoint>> requestedPoints = requestedDocuments.ToDictionary(
            static document => document.Key, static _ => new List<ManagedSequencePoint>(), PathComparer);
        var documentPaths = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (ManagedSequencePoint point in sequencePoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!documentPaths.TryGetValue(point.SourcePath, out string[]? matchingPaths))
            {
                string documentPath = _sourcePathMapper.Map(point.SourcePath);
                matchingPaths = [.. requestedPoints.Keys.Where(path => PathsEqual(documentPath, path))];
                documentPaths.Add(point.SourcePath, matchingPaths);
            }
            foreach (string path in matchingPaths)
            {
                requestedPoints[path].Add(point);
            }
        }
        foreach (IGrouping<string, SourceBreakpointDefinition> document in requestedDocuments)
        {
            ResolveDocumentLocations(requestedPoints[document.Key], document, result, cancellationToken);
        }
        return result;
    }

    private static void ResolveDocumentLocations(
        List<ManagedSequencePoint> points,
        IEnumerable<SourceBreakpointDefinition> definitions,
        Dictionary<int, SourceBreakpointLocation> result,
        CancellationToken cancellationToken)
    {
        IGrouping<int, ManagedSequencePoint>[] starts = [.. points.GroupBy(static point => point.StartLine)
            .OrderBy(static group => group.Key)];
        var active = new Stack<(IGrouping<int, ManagedSequencePoint> Points, int EndLine)>();
        int next = 0;
        foreach (SourceBreakpointDefinition definition in definitions.OrderBy(static definition => definition.RequestedLine))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int line = definition.RequestedLine;
            while (next < starts.Length && starts[next].Key <= line)
            {
                IGrouping<int, ManagedSequencePoint> group = starts[next++];
                active.Push((group, group.Max(static point => point.EndLine)));
            }
            while (active.TryPeek(out (IGrouping<int, ManagedSequencePoint> Points, int EndLine) candidate) &&
                candidate.EndLine < line)
            {
                _ = active.Pop();
            }
            // The most recent containing start wins; otherwise bind the next executable line.
            IGrouping<int, ManagedSequencePoint>? candidates = active.Count > 0
                ? active.Peek().Points
                : next < starts.Length ? starts[next] : null;
            if (candidates is null)
            {
                continue;
            }
            ManagedSequencePoint? best = null;
            int distance = int.MaxValue;
            foreach (ManagedSequencePoint candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.EndLine < line)
                {
                    continue;
                }
                int columnDistance = Math.Abs(candidate.StartColumn - (definition.RequestedColumn ?? 0));
                if (best is null || columnDistance < distance)
                {
                    best = candidate;
                    distance = columnDistance;
                }
            }
            if (best is not null)
            {
                result.Add(definition.Id, new SourceBreakpointLocation(best.MethodToken, checked((uint)best.IlOffset),
                    best.StartLine, best.StartColumn, best.EndLine));
            }
        }
    }
}
