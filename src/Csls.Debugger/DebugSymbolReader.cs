using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
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
    private static readonly Guid s_stateMachineHoistedLocalScopes =
        new("6DA9A61E-F8C7-4874-BE62-68BC5630DF71");
    private readonly PortablePdbReader? _portable;
    private readonly IReadOnlyList<PortablePdbReader> _portableDeltas = [];
    private readonly WindowsPdbReader? _windows;

    private DebugSymbolReader(
        DisposableOwner<PortablePdbReader> owner,
        IReadOnlyList<PortablePdbReader>? deltas = null)
    {
        PortablePdbReader portable = owner.Value
            ?? throw new InvalidOperationException("No Portable PDB reader is owned.");
        _portable = portable;
        _portableDeltas = deltas ?? [];
        StorageKind = portable.StorageKind;
        Path = portable.Path;
        _ = owner.Detach();
    }

    private DebugSymbolReader(DisposableOwner<WindowsPdbReader> owner)
    {
        WindowsPdbReader windows = owner.Value
            ?? throw new InvalidOperationException("No Windows PDB reader is owned.");
        _windows = windows;
        StorageKind = DebugSymbolStorageKind.Windows;
        Path = windows.Path;
        _ = owner.Detach();
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
    /// Gets the compiler-recorded user entry method from the validated symbols.
    /// </summary>
    /// <returns>The entry method token, or null when the symbols have no entry.</returns>
    internal uint? GetEntryPoint()
    {
        if (_windows is not null)
        {
            return _windows.GetEntryPoint();
        }

        MethodDefinitionHandle entry = GetPortableReader().Metadata.DebugMetadataHeader?.EntryPoint ?? default;
        return entry.IsNil ? null : checked((uint)MetadataTokens.GetToken(entry));
    }

    /// <summary>
    /// Opens identity-matched symbols for a file-backed managed module.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <param name="symbolPath">An explicit associated PDB candidate, or null.</param>
    /// <returns>An owned symbol reader, or null when matching symbols are unavailable.</returns>
    internal static DebugSymbolReader? TryOpen(
        string modulePath,
        string? symbolPath = null)
        => TryOpen(modulePath, symbolPath, []);

    /// <summary>
    /// Opens identity-matched base symbols and their ordered Portable PDB deltas.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <param name="symbolPath">An explicit associated PDB candidate, or null.</param>
    /// <param name="deltaImages">The immutable Portable PDB deltas in generation order.</param>
    /// <returns>An owned symbol reader, or null when matching symbols are unavailable.</returns>
    internal static DebugSymbolReader? TryOpen(
        string modulePath,
        string? symbolPath,
        IReadOnlyList<byte[]> deltaImages)
    {
        ArgumentNullException.ThrowIfNull(deltaImages);
        using var portableOwner = new DisposableOwner<PortablePdbReader>();
        portableOwner.Acquire(() => TryOpenPortable(modulePath, symbolPath));
        if (portableOwner.Value is not null)
        {
            IReadOnlyList<PortablePdbReader> deltas = OpenPortableDeltas(
                portableOwner.Value,
                deltaImages);
            return new DebugSymbolReader(portableOwner, deltas);
        }

        if (deltaImages.Count != 0)
        {
            throw new BadImageFormatException(
                "Portable PDB deltas require identity-matched base Portable PDB symbols.");
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

        using var windowsOwner = new DisposableOwner<WindowsPdbReader>();
        windowsOwner.Acquire(() => WindowsPdbReader.TryOpen(modulePath, candidate));
        if (windowsOwner.Value is null)
        {
            return null;
        }

        return new DebugSymbolReader(windowsOwner);
    }

    /// <summary>
    /// Opens a runtime-supplied in-memory Portable PDB image.
    /// </summary>
    /// <param name="image">The complete immutable Portable PDB image.</param>
    /// <returns>An owned symbol reader, or null when the image is not a Portable PDB.</returns>
    internal static DebugSymbolReader? TryOpen(byte[] image)
        => TryOpen(image, []);

    /// <summary>
    /// Opens Portable PDBs using the identity of a selected captured module image.
    /// </summary>
    /// <param name="peReader">The captured module image containing its debug directory.</param>
    /// <param name="modulePath">The recorded module path used to locate associated symbols.</param>
    /// <returns>An owned reader, or null when matching symbols are unavailable.</returns>
    internal static DebugSymbolReader? TryOpen(PEReader peReader, string modulePath)
    {
        using var owner = new DisposableOwner<PortablePdbReader>();
        try
        {
            bool localPath = System.IO.Path.IsPathFullyQualified(modulePath) &&
                !modulePath.StartsWith("\\\\", StringComparison.Ordinal) &&
                !modulePath.StartsWith("//", StringComparison.Ordinal);
            owner.Acquire(() => PortablePdbReader.TryOpen(peReader, modulePath, allowAssociatedSymbols: localPath));
            return owner.Value is null ? null : new DebugSymbolReader(owner);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Opens runtime-supplied base symbols and their ordered Portable PDB deltas.
    /// </summary>
    /// <param name="image">The complete immutable base Portable PDB image.</param>
    /// <param name="deltaImages">The immutable Portable PDB deltas in generation order.</param>
    /// <returns>An owned symbol reader, or null when the base image is not a Portable PDB.</returns>
    internal static DebugSymbolReader? TryOpen(
        byte[] image,
        IReadOnlyList<byte[]> deltaImages)
    {
        ArgumentNullException.ThrowIfNull(deltaImages);
        using var owner = new DisposableOwner<PortablePdbReader>();
        owner.Acquire(() => PortablePdbReader.TryOpen(image));
        if (owner.Value is null)
        {
            return null;
        }

        IReadOnlyList<PortablePdbReader> deltas = OpenPortableDeltas(
            owner.Value,
            deltaImages);
        return new DebugSymbolReader(owner, deltas);
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

        PortablePdbReader baseReader = GetPortableReader();
        var documents = new SortedDictionary<string, ManagedSymbolDocument>(
            StringComparer.Ordinal);
        foreach (ManagedSymbolDocument document in baseReader.Metadata.Documents.Select(handle =>
                     PortablePdbSourceDocumentReader.Read(
                         baseReader.Metadata,
                         handle,
                         baseReader.SourceLinkMappings)))
        {
            documents[document.Path] = document;
        }

        foreach (PortablePdbReader delta in _portableDeltas)
        {
            IReadOnlyList<KeyValuePair<string, string>> sourceLinkMappings =
                delta.SourceLinkMappings.Count == 0
                    ? baseReader.SourceLinkMappings
                    : delta.SourceLinkMappings;
            foreach (ManagedSymbolDocument document in delta.Metadata.Documents.Select(handle =>
                         PortablePdbSourceDocumentReader.Read(
                             delta.Metadata,
                             handle,
                             sourceLinkMappings)))
            {
                documents[document.Path] = document;
            }
        }

        return [.. documents.Values];
    }

    /// <summary>
    /// Reads ordered sequence points for one method or for the complete module.
    /// </summary>
    /// <param name="methodToken">The method token, or null to enumerate every method.</param>
    /// <param name="includeHidden">Whether to retain compiler-generated instruction boundaries.</param>
    /// <returns>The immutable ordered sequence points.</returns>
    internal IReadOnlyList<ManagedSequencePoint> GetSequencePoints(uint? methodToken, bool includeHidden = false)
    {
        if (_windows is not null)
        {
            return _windows.GetSequencePoints(methodToken, includeHidden);
        }

        IEnumerable<(uint Token, PortablePdbReader Reader, MethodDebugInformation Info)> methods =
            GetPortableMethods(methodToken);
        var result = new List<ManagedSequencePoint>();
        foreach ((uint token, PortablePdbReader portable, MethodDebugInformation method) in methods)
        {
            MetadataReader reader = portable.Metadata;
            foreach (SequencePoint point in method.GetSequencePoints())
            {
                if (point.IsHidden || point.StartLine == HiddenSequencePointLine)
                {
                    if (includeHidden)
                    {
                        result.Add(new ManagedSequencePoint(token, point.Offset, string.Empty,
                            HiddenSequencePointLine, 0, HiddenSequencePointLine, 0, Guid.Empty));
                    }

                    continue;
                }

                DocumentHandle document = point.Document.IsNil
                    ? method.Document
                    : point.Document;
                if (document.IsNil)
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
    /// Reads the method's document language independently of its sequence-point offsets.
    /// </summary>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <returns>The recorded document language, or an empty identity when unavailable.</returns>
    internal Guid GetDocumentLanguage(uint methodToken)
    {
        if (_windows is not null)
        {
            IReadOnlyList<ManagedSequencePoint> points = _windows.GetSequencePoints(methodToken, includeHidden: false);
            return points.Count == 0 ? Guid.Empty : points[0].LanguageId;
        }

        if (!TryGetPortableMethod(methodToken, out PortablePdbReader? portable, out MethodDefinitionHandle method))
        {
            return Guid.Empty;
        }

        MetadataReader metadata = portable.Metadata;
        MethodDebugInformation information = metadata.GetMethodDebugInformation(method.ToDebugInformationHandle());
        DocumentHandle document = information.Document;
        if (document.IsNil)
        {
            document = information.GetSequencePoints().Select(static point => point.Document)
                .FirstOrDefault(static handle => !handle.IsNil);
        }

        return document.IsNil ? Guid.Empty : metadata.GetGuid(metadata.GetDocument(document).Language);
    }

    /// <summary>
    /// Reads active local symbols for one method and IL instruction offset.
    /// </summary>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <param name="ilOffset">The current method-body IL offset.</param>
    /// <returns>Local source metadata keyed by runtime slot.</returns>
    internal IReadOnlyDictionary<int, ManagedSymbolVariable> GetLocalVariables(
        uint methodToken,
        uint ilOffset)
    {
        if (_windows is not null)
        {
            return _windows.GetLocalNames(methodToken, ilOffset).ToDictionary(
                static pair => pair.Key,
                static pair => new ManagedSymbolVariable(
                    pair.Value,
                    TupleCustomTypeInfo: null));
        }

        if (!TryGetPortableMethod(
            methodToken,
            out PortablePdbReader? portable,
            out MethodDefinitionHandle method))
        {
            return new Dictionary<int, ManagedSymbolVariable>();
        }

        MetadataReader reader = portable.Metadata;
        var result = new Dictionary<int, ManagedSymbolVariable>();
        foreach (LocalScope scope in reader.GetLocalScopes(method).Select(reader.GetLocalScope))
        {
            uint start = checked((uint)scope.StartOffset);
            uint end = checked((uint)(scope.StartOffset + scope.Length));
            if (ilOffset < start || ilOffset >= end)
            {
                continue;
            }

            foreach (LocalVariableHandle variableHandle in scope.GetLocalVariables())
            {
                LocalVariable variable = reader.GetLocalVariable(variableHandle);
                result[variable.Index] = new ManagedSymbolVariable(
                    reader.GetString(variable.Name),
                    ManagedTupleElementNameReader.ReadPortablePdb(reader, variableHandle));
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the active hoisted-local indexes from the selected Portable PDB method generation.
    /// </summary>
    /// <param name="methodToken">The physical state-machine method token.</param>
    /// <param name="ilOffset">The current instruction offset in that method.</param>
    /// <returns>Zero-based hoisted-local slots whose half-open scopes contain the instruction.</returns>
    internal IReadOnlySet<int> GetActiveHoistedLocalScopes(uint methodToken, uint ilOffset)
    {
        var active = new HashSet<int>();
        if (!TryGetPortableMethod(methodToken, out PortablePdbReader? portable, out MethodDefinitionHandle method))
        {
            return active;
        }

        MetadataReader reader = portable.Metadata;
        foreach (CustomDebugInformationHandle handle in reader.GetCustomDebugInformation(method))
        {
            CustomDebugInformation information = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(information.Kind) != s_stateMachineHoistedLocalScopes)
            {
                continue;
            }

            BlobReader blob = reader.GetBlobReader(information.Value);
            if (blob.Length % 8 != 0 || blob.Length / 8 > 64 * 1024)
            {
                throw new BadImageFormatException("The hoisted-local scope record is truncated or exceeds 65536 entries.");
            }

            for (int index = 0; blob.RemainingBytes > 0; index++)
            {
                uint start = blob.ReadUInt32();
                uint length = blob.ReadUInt32();
                if (start > int.MaxValue || length > int.MaxValue || (ulong)start + length > int.MaxValue)
                {
                    throw new BadImageFormatException("The hoisted-local scope exceeds its method's IL range.");
                }

                if (ilOffset >= start && ilOffset - start < length)
                {
                    active.Add(index);
                }
            }

            return active;
        }

        return active;
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

        if (!TryGetPortableMethod(
            methodToken,
            out PortablePdbReader? portable,
            out MethodDefinitionHandle method))
        {
            return null;
        }

        MetadataReader reader = portable.Metadata;
        MethodDefinitionHandle kickoff = reader
            .GetMethodDebugInformation(
                method.ToDebugInformationHandle())
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
        IReadOnlyList<ManagedAsyncAwaitPoint> points = GetAsyncAwaitPoints(methodToken);
        foreach (ManagedAsyncAwaitPoint candidate in points)
        {
            if (ilOffset <= candidate.YieldOffset)
            {
                point = candidate;
                return true;
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
    /// Reads the bounded compiler-recorded suspension and resumption locations for one method.
    /// </summary>
    internal IReadOnlyList<ManagedAsyncAwaitPoint> GetAsyncAwaitPoints(uint methodToken) => _windows is not null
        ? _windows.GetAsyncAwaitPoints(methodToken)
        : GetPortableAsyncAwaitPoints(methodToken);

    /// <summary>
    /// Releases the selected Portable or Windows PDB owner.
    /// </summary>
    public void Dispose()
    {
        _portable?.Dispose();
        foreach (PortablePdbReader delta in _portableDeltas)
        {
            delta.Dispose();
        }

        _windows?.Dispose();
    }

    private IEnumerable<(uint Token, PortablePdbReader Reader, MethodDebugInformation Info)>
        GetPortableMethods(uint? methodToken)
    {
        if (methodToken is uint selected)
        {
            if (TryGetPortableMethod(
                selected,
                out PortablePdbReader? portable,
                out MethodDefinitionHandle method))
            {
                yield return (
                    selected,
                    portable,
                    portable.Metadata.GetMethodDebugInformation(
                        method.ToDebugInformationHandle()));
            }

            yield break;
        }

        PortablePdbReader baseReader = GetPortableReader();
        var methods = new Dictionary<uint, (PortablePdbReader Reader, MethodDefinitionHandle Handle)>();
        int row = 0;
        foreach (MethodDebugInformationHandle _ in baseReader.Metadata.MethodDebugInformation)
        {
            row++;
            uint token = checked((uint)MetadataTokens.GetToken(
                MetadataTokens.MethodDefinitionHandle(row)));
            methods[token] = (baseReader, MetadataTokens.MethodDefinitionHandle(row));
        }

        foreach (PortablePdbReader delta in _portableDeltas)
        {
            int relativeRow = 0;
            foreach (EntityHandle handle in delta.Metadata.GetEditAndContinueMapEntries())
            {
                if (handle.Kind != HandleKind.MethodDebugInformation)
                {
                    continue;
                }

                relativeRow++;
                uint token = checked((uint)MetadataTokens.GetToken(
                    ((MethodDebugInformationHandle)handle).ToDefinitionHandle()));
                methods[token] = (delta, MetadataTokens.MethodDefinitionHandle(relativeRow));
            }
        }

        foreach ((uint token, (PortablePdbReader portable, MethodDefinitionHandle handle)) in
            methods.OrderBy(static pair => pair.Key))
        {
            yield return (
                token,
                portable,
                portable.Metadata.GetMethodDebugInformation(handle.ToDebugInformationHandle()));
        }
    }

    private List<ManagedAsyncAwaitPoint> GetPortableAsyncAwaitPoints(
        uint methodToken)
    {
        if (!TryGetPortableMethod(
            methodToken,
            out PortablePdbReader? portable,
            out MethodDefinitionHandle method))
        {
            return [];
        }

        MetadataReader reader = portable.Metadata;
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
                    MethodDefinitionTokenKind | checked((uint)resumeMethodRow)));
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

    private bool TryGetPortableMethod(
        uint methodToken,
        out PortablePdbReader portable,
        out MethodDefinitionHandle method)
    {
        if ((methodToken & 0xff000000) != MethodDefinitionTokenKind)
        {
            portable = null!;
            method = default;
            return false;
        }

        foreach (PortablePdbReader delta in _portableDeltas.Reverse())
        {
            int relativeRow = 0;
            foreach (EntityHandle handle in delta.Metadata.GetEditAndContinueMapEntries())
            {
                if (handle.Kind != HandleKind.MethodDebugInformation)
                {
                    continue;
                }

                relativeRow++;
                uint candidate = checked((uint)MetadataTokens.GetToken(
                    ((MethodDebugInformationHandle)handle).ToDefinitionHandle()));
                if (candidate == methodToken)
                {
                    portable = delta;
                    method = MetadataTokens.MethodDefinitionHandle(relativeRow);
                    return true;
                }
            }
        }

        PortablePdbReader baseReader = GetPortableReader();
        int rowNumber = checked((int)(methodToken & 0x00ffffff));
        if (rowNumber == 0 || rowNumber > baseReader.Metadata.MethodDebugInformation.Count)
        {
            portable = null!;
            method = default;
            return false;
        }

        portable = baseReader;
        method = MetadataTokens.MethodDefinitionHandle(rowNumber);
        return true;
    }

    private static List<PortablePdbReader> OpenPortableDeltas(
        PortablePdbReader baseReader,
        IReadOnlyList<byte[]> deltaImages)
    {
        if (deltaImages.Count == 0)
        {
            return [];
        }

        var deltas = new List<PortablePdbReader>(deltaImages.Count);
        var metadataReaders = new List<MetadataReader>(deltaImages.Count);
        try
        {
            foreach (PortablePdbReader delta in deltaImages.Select(static image =>
                         PortablePdbReader.TryOpen(image)
                         ?? throw new BadImageFormatException(
                             "A Hot Reload symbol generation is not a Portable PDB.")))
            {
                deltas.Add(delta);
                metadataReaders.Add(delta.Metadata);
            }

            _ = new MetadataAggregator(baseReader.Metadata, metadataReaders);
            return deltas;
        }
        catch (ArgumentException exception)
        {
            foreach (PortablePdbReader delta in deltas)
            {
                delta.Dispose();
            }

            throw new BadImageFormatException(
                "A Hot Reload symbol generation is not a valid minimal Portable PDB delta.",
                exception);
        }
        catch
        {
            foreach (PortablePdbReader delta in deltas)
            {
                delta.Dispose();
            }

            throw;
        }
    }
}
