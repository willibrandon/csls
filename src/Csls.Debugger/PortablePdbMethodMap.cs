using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Reads visible source sequence points for one managed method definition.
/// </summary>
internal static class PortablePdbMethodMap
{
    private const int HiddenSequencePointLine = 0x00feefee;

    /// <summary>
    /// Reads the ordered visible sequence points for a method.
    /// </summary>
    /// <param name="frame">The generation-bound frame and immutable symbol snapshot.</param>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <returns>The ordered visible source positions.</returns>
    internal static IReadOnlyList<ManagedSequencePoint> Read(
        ManagedFrameHandle frame,
        uint methodToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using PortablePdbReader? symbols = frame.OpenSymbols();
        if (symbols is null)
        {
            return [];
        }

        MetadataReader reader = symbols.Metadata;
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > reader.MethodDebugInformation.Count)
        {
            return [];
        }

        MethodDebugInformation method = reader.GetMethodDebugInformation(
            MetadataTokens.MethodDebugInformationHandle(rowNumber));
        var result = new List<ManagedSequencePoint>();
        foreach (SequencePoint point in method.GetSequencePoints())
        {
            DocumentHandle documentHandle = point.Document.IsNil
                ? method.Document
                : point.Document;
            if (point.IsHidden || point.StartLine == HiddenSequencePointLine ||
                documentHandle.IsNil)
            {
                continue;
            }

            string path = reader.GetString(reader.GetDocument(documentHandle).Name);
            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(new ManagedSequencePoint(
                    point.Offset,
                    path,
                    point.StartLine,
                    point.StartColumn,
                    point.EndLine,
                    point.EndColumn));
            }
        }

        return result;
    }
}
