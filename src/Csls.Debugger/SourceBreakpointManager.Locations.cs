using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Resolves source breakpoint requests to Portable PDB sequence points.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private Dictionary<int, SourceBreakpointLocation> ResolveLocations(
        MetadataReader reader,
        IReadOnlyList<SourceBreakpointDefinition> definitions)
    {
        var result = new Dictionary<int, SourceBreakpointLocation>();
        int rowNumber = 0;
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            rowNumber++;
            MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                if (point.IsHidden || point.StartLine == HiddenSequencePointLine)
                {
                    continue;
                }

                DocumentHandle documentHandle = point.Document.IsNil ? method.Document : point.Document;
                if (documentHandle.IsNil)
                {
                    continue;
                }

                string documentPath = _sourcePathMapper.Map(
                    reader.GetString(reader.GetDocument(documentHandle).Name));
                foreach (SourceBreakpointDefinition definition in definitions)
                {
                    if (!PathsEqual(documentPath, definition.SourcePath) ||
                        !IsBetterLocation(definition, point, result))
                    {
                        continue;
                    }

                    result[definition.Id] = new SourceBreakpointLocation(
                        checked((uint)MetadataTokens.GetToken(
                            MetadataTokens.MethodDefinitionHandle(rowNumber))),
                        checked((uint)point.Offset),
                        point.StartLine,
                        point.StartColumn,
                        point.EndLine);
                }
            }
        }

        return result;
    }

    private static bool IsBetterLocation(
        SourceBreakpointDefinition definition,
        SequencePoint candidate,
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
