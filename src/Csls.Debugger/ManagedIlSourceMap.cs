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
    /// <param name="frame">The generation-bound frame and immutable symbol snapshot.</param>
    /// <returns>Visible source locations keyed by exact IL offset.</returns>
    internal static IReadOnlyDictionary<int, ManagedFrameLocation> Read(ManagedFrameHandle frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using PortablePdbReader? symbols = frame.OpenSymbols();
        if (symbols is null)
        {
            return new Dictionary<int, ManagedFrameLocation>();
        }

        MetadataReader reader = symbols.Metadata;
        int rowNumber = checked((int)(frame.MethodToken & 0x00ffffff));
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
                Name = frame.Name,
                ModulePath = frame.ModulePath,
                ModuleId = frame.ModuleId,
                ModuleImage = frame.ModuleImage,
                SymbolImage = frame.SymbolImage,
                SourcePath = reader.GetString(document.Name),
                Line = point.StartLine,
                Column = point.StartColumn
            };
        }

        return result;
    }
}
