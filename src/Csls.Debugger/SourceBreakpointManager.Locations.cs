namespace Csls.Debugger;

/// <summary>
/// Resolves source breakpoint requests to managed-symbol sequence points.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private Dictionary<int, SourceBreakpointLocation> ResolveLocations(
        IReadOnlyList<ManagedSequencePoint> sequencePoints,
        IReadOnlyList<SourceBreakpointDefinition> definitions)
    {
        var result = new Dictionary<int, SourceBreakpointLocation>();
        foreach (ManagedSequencePoint point in sequencePoints)
        {
            string documentPath = _sourcePathMapper.Map(point.SourcePath);
            foreach (SourceBreakpointDefinition definition in definitions)
            {
                if (!PathsEqual(documentPath, definition.SourcePath) ||
                    !IsBetterLocation(definition, point, result))
                {
                    continue;
                }

                result[definition.Id] = new SourceBreakpointLocation(
                    point.MethodToken,
                    checked((uint)point.IlOffset),
                    point.StartLine,
                    point.StartColumn,
                    point.EndLine);
            }
        }

        return result;
    }

    private static bool IsBetterLocation(
        SourceBreakpointDefinition definition,
        ManagedSequencePoint candidate,
        Dictionary<int, SourceBreakpointLocation> current)
    {
        bool candidateContainsLine = definition.RequestedLine >= candidate.StartLine &&
            definition.RequestedLine <= candidate.EndLine;
        if (!candidateContainsLine && candidate.StartLine < definition.RequestedLine)
        {
            return false;
        }

        if (!current.TryGetValue(definition.Id, out SourceBreakpointLocation? existing))
        {
            return true;
        }

        bool existingContainsLine = definition.RequestedLine >= existing.Line &&
            definition.RequestedLine <= existing.EndLine;
        if (candidateContainsLine != existingContainsLine)
        {
            return candidateContainsLine;
        }

        int candidateDistance = Math.Abs(candidate.StartLine - definition.RequestedLine);
        int existingDistance = Math.Abs(existing.Line - definition.RequestedLine);
        if (candidateDistance != existingDistance)
        {
            return candidateDistance < existingDistance;
        }

        int requestedColumn = definition.RequestedColumn ?? 0;
        return Math.Abs(candidate.StartColumn - requestedColumn) <
            Math.Abs(existing.Column - requestedColumn);
    }
}
