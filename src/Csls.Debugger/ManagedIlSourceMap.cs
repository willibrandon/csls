namespace Csls.Debugger;

/// <summary>
/// Maps managed IL offsets to validated symbol source positions.
/// </summary>
internal static class ManagedIlSourceMap
{
    /// <summary>
    /// Reads every visible source mapping for one method definition.
    /// </summary>
    /// <param name="frame">The generation-bound frame and immutable symbol snapshot.</param>
    /// <returns>Visible source locations keyed by exact IL offset.</returns>
    internal static IReadOnlyDictionary<int, ManagedFrameLocation> Read(ManagedFrameHandle frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using DebugSymbolReader? symbols = frame.OpenSymbols();
        if (symbols is null)
        {
            return new Dictionary<int, ManagedFrameLocation>();
        }

        var result = new Dictionary<int, ManagedFrameLocation>();
        foreach (ManagedSequencePoint point in symbols.GetSequencePoints(frame.MethodToken))
        {
            result[point.IlOffset] = new ManagedFrameLocation
            {
                Name = frame.Name,
                ModulePath = frame.ModulePath,
                ModuleId = frame.ModuleId,
                ModuleImage = frame.ModuleImage,
                SymbolImage = frame.SymbolImage,
                SourcePath = point.SourcePath,
                Line = point.StartLine,
                Column = point.StartColumn,
                ExpressionLanguage = ManagedExpressionLanguageResolver.Resolve(point.LanguageId)
            };
        }

        return result;
    }
}
