using Csls.Debugger.Contracts;
using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Enumerates loaded source documents and executable Portable PDB locations.
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
        var paths = new HashSet<string>(PathComparer);
        foreach (CorDebugLoadedModule module in _modules.Values.OrderBy(static module => module.Id))
        {
            if (module.Path is null)
            {
                continue;
            }

            try
            {
                AddLoadedSources(module.Path, paths);
            }
            catch (Exception exception) when (IsSymbolReadException(exception))
            {
            }

            if (paths.Count >= MaximumSourceCount)
            {
                break;
            }
        }

        return paths
            .Order(PathComparer)
            .Select(static path => new DebugSourceInfo(Path.GetFileName(path), path))
            .ToArray();
    }

    private static void AddLoadedSources(string modulePath, HashSet<string> paths)
    {
        using var symbols = PortablePdbReader.TryOpen(modulePath);
        if (symbols is null)
        {
            return;
        }

        foreach (DocumentHandle handle in symbols.Metadata.Documents)
        {
            string? path = GetDocumentPath(symbols.Metadata, handle);
            if (path is not null && paths.Add(path) && paths.Count >= MaximumSourceCount)
            {
                return;
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

    private static void AddBreakpointLocations(
        CorDebugLoadedModule module,
        string sourcePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        HashSet<DebugBreakpointLocation> locations)
    {
        if (module.Path is null)
        {
            return;
        }

        try
        {
            var symbols = PortablePdbReader.TryOpen(module.Path);
            try
            {
                if (symbols is null)
                {
                    return;
                }

                MetadataReader reader = symbols.Metadata;
                foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
                {
                    MethodDebugInformation method = reader.GetMethodDebugInformation(handle);
                    foreach (SequencePoint point in method.GetSequencePoints())
                    {
                        if (!IsMatchingLocation(
                            reader,
                            method,
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

    private static bool IsMatchingLocation(
        MetadataReader reader,
        MethodDebugInformation method,
        SequencePoint point,
        string sourcePath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn)
    {
        if (point.IsHidden || point.StartLine == HiddenSequencePointLine ||
            ComparePosition(point.StartLine, point.StartColumn, startLine, startColumn) < 0 ||
            ComparePosition(point.StartLine, point.StartColumn, endLine, endColumn) > 0)
        {
            return false;
        }

        DocumentHandle document = point.Document.IsNil ? method.Document : point.Document;
        return !document.IsNil &&
            GetDocumentPath(reader, document) is string documentPath &&
            PathsEqual(documentPath, sourcePath);
    }

    private static string? GetDocumentPath(MetadataReader reader, DocumentHandle handle)
    {
        string path = reader.GetString(reader.GetDocument(handle).Name);
        return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            ? null
            : Path.GetFullPath(path);
    }

    private static string NormalizeAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The source document path must be absolute.", nameof(path));
        }

        return Path.GetFullPath(path);
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
        exception is IOException or UnauthorizedAccessException or BadImageFormatException;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
