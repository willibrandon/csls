using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Csls.Debugger;

/// <summary>
/// Maps managed IL offsets to validated Portable PDB source positions.
/// </summary>
internal static class ManagedIlSourceMap
{
    private const int HiddenSequencePointLine = 0x00feefee;

    /// <summary>
    /// Reads every visible source mapping for one method definition.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <param name="methodName">The language-neutral method display name.</param>
    /// <returns>Visible source locations keyed by exact IL offset.</returns>
    internal static IReadOnlyDictionary<int, ManagedFrameLocation> Read(
        string modulePath,
        uint methodToken,
        string methodName)
    {
        using var symbols = PortablePdbReader.TryOpen(modulePath);
        if (symbols is null)
        {
            return new Dictionary<int, ManagedFrameLocation>();
        }

        MetadataReader reader = symbols.Metadata;
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > reader.MethodDebugInformation.Count)
        {
            return new Dictionary<int, ManagedFrameLocation>();
        }

        var result = new Dictionary<int, ManagedFrameLocation>();
        MethodDebugInformation method = reader.GetMethodDebugInformation(
            MetadataTokens.MethodDebugInformationHandle(rowNumber));
        foreach (SequencePoint point in method.GetSequencePoints())
        {
            if (point.IsHidden || point.StartLine == HiddenSequencePointLine ||
                point.Document.IsNil)
            {
                continue;
            }

            Document document = reader.GetDocument(point.Document);
            result[point.Offset] = new ManagedFrameLocation
            {
                Name = methodName,
                ModulePath = modulePath,
                SourcePath = reader.GetString(document.Name),
                Line = point.StartLine,
                Column = point.StartColumn
            };
        }

        return result;
    }
}
