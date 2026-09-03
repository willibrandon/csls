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
    private const int MaximumAsyncAwaitCount = 16 * 1024;
    private const uint MethodDefinitionTokenKind = 0x06000000;
    private static readonly Guid s_asyncMethodSteppingInformation =
        new("54FD2AC5-E925-401A-9C2A-F94F171072F8");
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
                    Document sourceDocument = reader.GetDocument(document);
                    result.Add(new ManagedSequencePoint(
                        token,
                        point.Offset,
                        path,
                        point.StartLine,
                        point.StartColumn,
                        point.EndLine,
                        point.EndColumn,
                        sourceDocument.Language.IsNil
                            ? Guid.Empty
                            : reader.GetGuid(sourceDocument.Language)));
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
    /// Gets the user-authored method that produced a state-machine MoveNext method.
    /// </summary>
    /// <param name="methodToken">The candidate state-machine method token.</param>
    /// <returns>The kickoff method token, or null for an ordinary method.</returns>
    internal uint? GetStateMachineKickoffMethod(uint methodToken)
    {
        if (_windows is not null)
        {
            return _windows.GetStateMachineKickoffMethod(methodToken);
        }

        MetadataReader reader = GetPortableReader().Metadata;
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > reader.MethodDebugInformation.Count)
        {
            return null;
        }

        MethodDefinitionHandle kickoff = reader
            .GetMethodDebugInformation(
                MetadataTokens.MethodDebugInformationHandle(rowNumber))
            .GetStateMachineKickoffMethod();
        return kickoff.IsNil
            ? null
            : checked((uint)MetadataTokens.GetToken(kickoff));
    }

    /// <summary>
    /// Finds the next compiler-recorded asynchronous suspension after an IL offset.
    /// </summary>
    /// <param name="methodToken">The active state-machine method token.</param>
    /// <param name="ilOffset">The active IL instruction offset.</param>
    /// <param name="point">Receives the matching yield and resume locations.</param>
    /// <returns>True when a later await exists in the active method.</returns>
    internal bool TryGetNextAsyncAwait(
        uint methodToken,
        uint ilOffset,
        out ManagedAsyncAwaitPoint point)
    {
        IReadOnlyList<ManagedAsyncAwaitPoint> points = _windows is not null
            ? _windows.GetAsyncAwaitPoints(methodToken)
            : GetPortableAsyncAwaitPoints(methodToken);
        foreach (ManagedAsyncAwaitPoint candidate in points)
        {
            if (ilOffset <= candidate.YieldOffset)
            {
                ManagedSequencePoint? resumed = GetSequencePoints(
                    candidate.ResumeMethodToken).FirstOrDefault(sequencePoint =>
                        sequencePoint.IlOffset >= candidate.ResumeOffset);
                if (resumed is not null)
                {
                    point = candidate with
                    {
                        ResumeStopOffset = checked((uint)resumed.IlOffset)
                    };
                    return true;
                }

                break;
            }

            if (ilOffset < candidate.ResumeOffset)
            {
                break;
            }
        }

        point = default;
        return false;
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

    private List<ManagedAsyncAwaitPoint> GetPortableAsyncAwaitPoints(
        uint methodToken)
    {
        MetadataReader reader = GetPortableReader().Metadata;
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > reader.MethodDebugInformation.Count)
        {
            return [];
        }

        MethodDefinitionHandle method = MetadataTokens.MethodDefinitionHandle(rowNumber);
        foreach (CustomDebugInformationHandle handle in reader.GetCustomDebugInformation(method))
        {
            CustomDebugInformation information = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(information.Kind) != s_asyncMethodSteppingInformation)
            {
                continue;
            }

            BlobReader blob = reader.GetBlobReader(information.Value);
            if (blob.RemainingBytes < sizeof(uint))
            {
                throw new BadImageFormatException(
                    "The async stepping record is missing its catch-handler offset.");
            }

            _ = blob.ReadUInt32();
            var result = new List<ManagedAsyncAwaitPoint>();
            while (blob.RemainingBytes > 0)
            {
                if (blob.RemainingBytes < 2 * sizeof(uint) ||
                    result.Count == MaximumAsyncAwaitCount)
                {
                    throw new BadImageFormatException(
                        $"The async stepping record exceeds {MaximumAsyncAwaitCount} entries or is truncated.");
                }

                uint yieldOffset = blob.ReadUInt32();
                uint resumeOffset = blob.ReadUInt32();
                int resumeMethodRow = blob.ReadCompressedInteger();
                if (resumeMethodRow <= 0 || resumeOffset < yieldOffset)
                {
                    throw new BadImageFormatException(
                        "The async stepping record contains an invalid continuation.");
                }

                result.Add(new ManagedAsyncAwaitPoint(
                    yieldOffset,
                    resumeOffset,
                    MethodDefinitionTokenKind | checked((uint)resumeMethodRow),
                    ResumeStopOffset: resumeOffset));
            }

            return result;
        }

        return [];
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
