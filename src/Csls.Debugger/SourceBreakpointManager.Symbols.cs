using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Enumerates loaded source documents and executable managed-symbol locations.
/// </summary>
internal sealed partial class SourceBreakpointManager
{
    private const int MaximumSourceCount = 65_536;
    private const int MaximumBreakpointLocationCount = 65_536;

    /// <summary>
    /// Gets the distinct source documents represented by currently loaded symbols.
    /// </summary>
    /// <returns>The normalized source document snapshot.</returns>
    internal IReadOnlyList<DebugSourceInfo> GetLoadedSources()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var sources = new Dictionary<string, DebugSourceInfo>(PathComparer);
        foreach (CorDebugLoadedModule module in _modules.Values.OrderBy(static module => module.Id))
        {
            try
            {
                AddLoadedSources(module, sources);
            }
            catch (Exception exception) when (IsSymbolReadException(exception))
            {
            }

            if (sources.Count >= MaximumSourceCount)
            {
                break;
            }
        }

        return sources.Values
            .OrderBy(static source => source.Path ?? source.Name, PathComparer)
            .ToArray();
    }

    private void AddLoadedSources(
        CorDebugLoadedModule module,
        Dictionary<string, DebugSourceInfo> sources)
    {
        using DebugSymbolReader? symbols = OpenSymbols(module);
        if (symbols is null)
        {
            return;
        }

        foreach (ManagedSymbolDocument document in symbols.GetDocuments())
        {
            string? path = GetDocumentPath(document.Path);
            if (path is not null && !sources.ContainsKey(path))
            {
                DebugSourceInfo source = RegisterSource(
                    GetSourceModuleKey(module),
                    document).Info;
                sources.Add(path, source);
                if (sources.Count >= MaximumSourceCount)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Gets executable sequence-point locations in one inclusive source range.
    /// </summary>
    /// <param name="sourcePath">The normalized absolute source document path.</param>
    /// <param name="startLine">The one-based inclusive start line.</param>
    /// <param name="startColumn">The one-based inclusive start column.</param>
    /// <param name="endLine">The one-based inclusive end line.</param>
    /// <param name="endColumn">The one-based inclusive end column.</param>
    /// <returns>The distinct ordered executable locations.</returns>
    internal IReadOnlyList<DebugBreakpointLocation> GetBreakpointLocations(
        string sourcePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        string normalizedPath = NormalizeAbsolutePath(sourcePath);
        ValidateRange(startLine, startColumn, endLine, endColumn);
        var locations = new HashSet<DebugBreakpointLocation>();
        foreach (CorDebugLoadedModule module in _modules.Values.OrderBy(static module => module.Id))
        {
            AddBreakpointLocations(
                module,
                normalizedPath,
                startLine,
                startColumn,
                endLine,
                endColumn,
                locations);
            if (locations.Count >= MaximumBreakpointLocationCount)
            {
                break;
            }
        }

        return locations
            .OrderBy(static location => location.Line)
            .ThenBy(static location => location.Column)
            .ThenBy(static location => location.EndLine)
            .ThenBy(static location => location.EndColumn)
            .ToArray();
    }

    private void AddBreakpointLocations(
        CorDebugLoadedModule module,
        string sourcePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        HashSet<DebugBreakpointLocation> locations)
    {
        try
        {
            DebugSymbolReader? symbols = OpenSymbols(module);
            try
            {
                if (symbols is null)
                {
                    return;
                }

                foreach (ManagedSequencePoint point in symbols.GetSequencePoints(methodToken: null))
                {
                    if (!IsMatchingLocation(
                        point,
                        sourcePath,
                        startLine,
                        startColumn,
                        endLine,
                        endColumn))
                    {
                        continue;
                    }

                    _ = locations.Add(new DebugBreakpointLocation(
                        point.StartLine,
                        point.StartColumn,
                        point.EndLine,
                        point.EndColumn));
                    if (locations.Count >= MaximumBreakpointLocationCount)
                    {
                        return;
                    }
                }
            }
            finally
            {
                symbols?.Dispose();
            }
        }
        catch (Exception exception) when (IsSymbolReadException(exception))
        {
        }
    }

    private bool IsMatchingLocation(
        ManagedSequencePoint point,
        string sourcePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        if (ComparePosition(point.StartLine, point.StartColumn, startLine, startColumn) < 0 ||
            ComparePosition(point.StartLine, point.StartColumn, endLine, endColumn) > 0)
        {
            return false;
        }

        return GetDocumentPath(point.SourcePath) is string documentPath &&
            PathsEqual(documentPath, sourcePath);
    }

    private string? GetDocumentPath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : _sourcePathMapper.Map(path);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!SourcePathMapper.IsAbsolutePath(path))
        {
            throw new ArgumentException("The source document path must be absolute.", nameof(path));
        }

        return SourcePathMapper.NormalizePath(path);
    }

    private static void ValidateRange(
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(startColumn, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endColumn, 1);
        if (ComparePosition(startLine, startColumn, endLine, endColumn) > 0)
        {
            throw new ArgumentException("The source range end must not precede its start.");
        }
    }

    private static int ComparePosition(int leftLine, int leftColumn, int rightLine, int rightColumn) =>
        leftLine != rightLine ? leftLine.CompareTo(rightLine) : leftColumn.CompareTo(rightColumn);

    private static bool IsSymbolReadException(Exception exception) =>
        DebugSymbolReader.IsReadFailure(exception);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
