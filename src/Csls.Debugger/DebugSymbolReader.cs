using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Presents Portable and Windows PDBs through one immutable managed-symbol model.
/// </summary>
internal sealed class DebugSymbolReader : IDisposable
{
    private const int HiddenSequencePointLine = 0x00feefee;
    private readonly PortablePdbReader? _portable;
    private readonly WindowsPdbReader? _windows;

    private DebugSymbolReader(PortablePdbReader portable)
    {
        _portable = portable;
        StorageKind = portable.StorageKind;
        Path = portable.Path;
    }

    private DebugSymbolReader(WindowsPdbReader windows)
    {
        _windows = windows;
        StorageKind = DebugSymbolStorageKind.Windows;
        Path = windows.Path;
    }

    /// <summary>
    /// Gets the physical symbol storage kind.
    /// </summary>
    internal DebugSymbolStorageKind StorageKind { get; }

    /// <summary>
    /// Gets the associated PDB path, or null for embedded or in-memory symbols.
    /// </summary>
    internal string? Path { get; }

    /// <summary>
    /// Opens identity-matched symbols for a file-backed managed module.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <param name="symbolPath">An explicit associated PDB candidate, or null.</param>
    /// <returns>An owned symbol reader, or null when matching symbols are unavailable.</returns>
    internal static DebugSymbolReader? TryOpen(
        string modulePath,
        string? symbolPath = null)
    {
        PortablePdbReader? portable = null;
        WindowsPdbReader? windows = null;
        try
        {
            portable = TryOpenPortable(modulePath, symbolPath);
            if (portable is not null)
            {
                var result = new DebugSymbolReader(portable);
                portable = null;
                return result;
            }

            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            string? candidate = symbolPath ?? GetAdjacentSymbolPath(modulePath);
            if (candidate is null)
            {
                return null;
            }

            windows = WindowsPdbReader.TryOpen(modulePath, candidate);
            if (windows is null)
            {
                return null;
            }

            var windowsResult = new DebugSymbolReader(windows);
            windows = null;
            return windowsResult;
        }
        finally
        {
            portable?.Dispose();
            windows?.Dispose();
        }
    }

    /// <summary>
    /// Opens a runtime-supplied in-memory Portable PDB image.
    /// </summary>
    /// <param name="image">The complete immutable Portable PDB image.</param>
    /// <returns>An owned symbol reader, or null when the image is not a Portable PDB.</returns>
    internal static DebugSymbolReader? TryOpen(byte[] image)
    {
        PortablePdbReader? portable = null;
        try
        {
            portable = PortablePdbReader.TryOpen(image);
            if (portable is null)
            {
                return null;
            }

            var result = new DebugSymbolReader(portable);
            portable = null;
            return result;
        }
        finally
        {
            portable?.Dispose();
        }
    }

    /// <summary>
    /// Determines whether an exception represents unavailable or unreadable symbol data.
    /// </summary>
    /// <param name="exception">The exception raised while locating or reading symbols.</param>
    /// <returns>True when callers may safely continue without the affected symbols.</returns>
    internal static bool IsReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or BadImageFormatException or
            InvalidDataException or COMException or DllNotFoundException or
            EntryPointNotFoundException or NotSupportedException or PlatformNotSupportedException or
            OverflowException;

    /// <summary>
    /// Reads every bounded source document represented by the PDB.
    /// </summary>
    /// <returns>The immutable source-document snapshot.</returns>
    internal IReadOnlyList<ManagedSymbolDocument> GetDocuments()
    {
        if (_windows is not null)
        {
            return _windows.GetDocuments();
        }

        PortablePdbReader portable = GetPortableReader();
        return [.. portable.Metadata.Documents.Select(handle =>
            PortablePdbSourceDocumentReader.Read(
                portable.Metadata,
                handle,
                portable.SourceLinkMappings))];
    }

    /// <summary>
    /// Reads visible sequence points for one method or for the complete module.
    /// </summary>
    /// <param name="methodToken">The method token, or null to enumerate every method.</param>
    /// <returns>The immutable ordered visible sequence points.</returns>
    internal IReadOnlyList<ManagedSequencePoint> GetSequencePoints(uint? methodToken)
    {
        if (_windows is not null)
        {
            return _windows.GetSequencePoints(methodToken);
        }

        MetadataReader reader = GetPortableReader().Metadata;
        IEnumerable<(uint Token, MethodDebugInformation Info)> methods =
            GetPortableMethods(reader, methodToken);
        var result = new List<ManagedSequencePoint>();
        foreach ((uint token, MethodDebugInformation method) in methods)
        {
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                DocumentHandle document = point.Document.IsNil
                    ? method.Document
                    : point.Document;
                if (point.IsHidden || point.StartLine == HiddenSequencePointLine ||
                    document.IsNil)
                {
                    continue;
                }

                string path = reader.GetString(reader.GetDocument(document).Name);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.Add(new ManagedSequencePoint(
                        token,
                        point.Offset,
                        path,
                        point.StartLine,
                        point.StartColumn,
                        point.EndLine,
                        point.EndColumn));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Reads active local names for one method and IL instruction offset.
    /// </summary>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <param name="ilOffset">The current method-body IL offset.</param>
    /// <returns>Local names keyed by runtime slot.</returns>
    internal IReadOnlyDictionary<int, string> GetLocalNames(
        uint methodToken,
        uint ilOffset)
    {
        if (_windows is not null)
        {
            return _windows.GetLocalNames(methodToken, ilOffset);
        }

        MetadataReader reader = GetPortableReader().Metadata;
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > reader.MethodDebugInformation.Count)
        {
            return new Dictionary<int, string>();
        }

        MethodDefinitionHandle method = MetadataTokens.MethodDefinitionHandle(rowNumber);
        var result = new Dictionary<int, string>();
        foreach (LocalScopeHandle scopeHandle in reader.GetLocalScopes(method))
        {
            LocalScope scope = reader.GetLocalScope(scopeHandle);
            uint start = checked((uint)scope.StartOffset);
            uint end = checked((uint)(scope.StartOffset + scope.Length));
            if (ilOffset < start || ilOffset >= end)
            {
                continue;
            }

            foreach (LocalVariableHandle variableHandle in scope.GetLocalVariables())
            {
                LocalVariable variable = reader.GetLocalVariable(variableHandle);
                result[variable.Index] = reader.GetString(variable.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Releases the selected Portable or Windows PDB owner.
    /// </summary>
    public void Dispose()
    {
        _portable?.Dispose();
        _windows?.Dispose();
    }

    private static IEnumerable<(uint Token, MethodDebugInformation Info)>
        GetPortableMethods(MetadataReader reader, uint? methodToken)
    {
        if (methodToken is uint selected)
        {
            int rowNumber = checked((int)(selected & 0x00ffffff));
            if (rowNumber > 0 && rowNumber <= reader.MethodDebugInformation.Count)
            {
                yield return (
                    selected,
                    reader.GetMethodDebugInformation(
                        MetadataTokens.MethodDebugInformationHandle(rowNumber)));
            }

            yield break;
        }

        int row = 0;
        foreach (MethodDebugInformationHandle handle in reader.MethodDebugInformation)
        {
            row++;
            yield return (
                checked((uint)MetadataTokens.GetToken(
                    MetadataTokens.MethodDefinitionHandle(row))),
                reader.GetMethodDebugInformation(handle));
        }
    }

    private static string? GetAdjacentSymbolPath(string modulePath)
    {
        CodeViewSymbolReference? reference = PortablePdbReader.ReadCodeViewReference(modulePath);
        string? directory = System.IO.Path.GetDirectoryName(modulePath);
        return reference is null || directory is null
            ? null
            : System.IO.Path.Join(directory, reference.FileName);
    }

    private static PortablePdbReader? TryOpenPortable(
        string modulePath,
        string? symbolPath)
    {
        try
        {
            return PortablePdbReader.TryOpen(modulePath, symbolPath);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    private PortablePdbReader GetPortableReader() => _portable
        ?? throw new ObjectDisposedException(nameof(DebugSymbolReader));
}
