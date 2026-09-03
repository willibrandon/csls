using Csls.Debugger.Contracts;
using Microsoft.DiaSymReader;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Reads an identity-matched Windows PDB through Microsoft's native DiaSymReader.
/// </summary>
internal sealed class WindowsPdbReader : IDisposable
{
    private const int HiddenSequencePointLine = 0x00feefee;
    private const int MaximumDocumentCount = 65_536;
    private const int MaximumNameCharacters = 32 * 1024;
    private const int MaximumScopeItemCount = 65_536;
    private const int MaximumSequencePointCount = 1_048_576;
    private const int MaximumSourceBytes = 32 * 1024 * 1024;
    private static readonly Guid s_sha1Algorithm =
        new("FF1816EC-AA5E-4D10-87F7-6F4963833460");
    private static readonly Guid s_sha256Algorithm =
        new("8829D00F-11B8-4213-878B-770E8597AC16");
    private readonly PEReader _peReader;
    private readonly FileStream _pdbStream;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _sourceLinkMappings;
    private ISymUnmanagedReader5? _reader;

    private WindowsPdbReader(
        string path,
        PEReader peReader,
        FileStream pdbStream,
        ISymUnmanagedReader5 reader)
    {
        Path = path;
        _peReader = peReader;
        _pdbStream = pdbStream;
        _reader = reader;
        _sourceLinkMappings = ReadSourceLinkMappings(reader);
    }

    /// <summary>
    /// Gets the normalized associated Windows PDB path.
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// Opens a Windows PDB only when its CodeView identity matches the managed PE.
    /// </summary>
    /// <param name="modulePath">The absolute managed PE path.</param>
    /// <param name="symbolPath">The absolute Windows PDB candidate path.</param>
    /// <returns>An owned reader, or null when the candidate is not a matching Windows PDB.</returns>
    internal static WindowsPdbReader? TryOpen(string modulePath, string symbolPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolPath);
        if (!OperatingSystem.IsWindows() ||
            !System.IO.Path.IsPathFullyQualified(modulePath) ||
            !System.IO.Path.IsPathFullyQualified(symbolPath) ||
            !File.Exists(modulePath) ||
            !File.Exists(symbolPath))
        {
            return null;
        }

        var moduleStream = new FileStream(
            modulePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        var peReader = new PEReader(moduleStream);
        var pdbStream = new FileStream(
            symbolPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        ISymUnmanagedReader5? reader = null;
        try
        {
            CodeViewSymbolReference? reference = PortablePdbReader.ReadCodeViewReference(modulePath);
            if (reference is null)
            {
                return null;
            }

            reader = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(
                pdbStream,
                new WindowsPdbMetadataProvider(peReader.GetMetadataReader()));
            int matchResult = reader.MatchesModule(
                reference.Signature,
                reference.Stamp,
                reference.Age,
                out bool matches);
            if (matchResult < 0 || !matches)
            {
                return null;
            }

            var result = new WindowsPdbReader(
                System.IO.Path.GetFullPath(symbolPath),
                peReader,
                pdbStream,
                reader);
            reader = null;
            peReader = null!;
            pdbStream = null!;
            return result;
        }
        finally
        {
            DisposeComObject(reader);
            pdbStream?.Dispose();
            peReader?.Dispose();
        }
    }

    /// <summary>
    /// Reads every bounded source document from the Windows PDB.
    /// </summary>
    /// <returns>The immutable source-document snapshot.</returns>
    internal IReadOnlyList<ManagedSymbolDocument> GetDocuments()
    {
        ISymUnmanagedReader5 reader = GetReader();
        ThrowIfFailed(
            reader.GetDocuments(0, out int count, null!),
            "ISymUnmanagedReader.GetDocuments");
        ValidateCount(count, MaximumDocumentCount, "document");
        if (count == 0)
        {
            return [];
        }

        var documents = new ISymUnmanagedDocument[count];
        ThrowIfFailed(
            reader.GetDocuments(documents.Length, out int read, documents),
            "ISymUnmanagedReader.GetDocuments");
        ValidateReadCount(read, documents.Length, "document");
        var result = new List<ManagedSymbolDocument>(read);
        try
        {
            for (int index = 0; index < read; index++)
            {
                result.Add(ReadDocument(documents[index], _sourceLinkMappings));
            }

            return result;
        }
        finally
        {
            foreach (ISymUnmanagedDocument document in documents.Take(read))
            {
                DisposeComObject(document);
            }
        }
    }

    /// <summary>
    /// Reads visible sequence points for one method or for the complete module.
    /// </summary>
    /// <param name="methodToken">The method token, or null to enumerate every method.</param>
    /// <returns>The immutable ordered visible sequence points.</returns>
    internal IReadOnlyList<ManagedSequencePoint> GetSequencePoints(uint? methodToken)
    {
        MetadataReader metadata = _peReader.GetMetadataReader();
        IEnumerable<uint> tokens = methodToken is uint selected
            ? [selected]
            : metadata.MethodDefinitions.Select(static handle =>
                checked((uint)MetadataTokens.GetToken(handle)));
        var result = new List<ManagedSequencePoint>();
        foreach (uint token in tokens)
        {
            ReadMethodSequencePoints(token, result);
            if (result.Count > MaximumSequencePointCount)
            {
                throw new InvalidDataException(
                    $"The Windows PDB exceeds the {MaximumSequencePointCount}-sequence-point limit.");
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
    internal IReadOnlyDictionary<int, string> GetLocalNames(uint methodToken, uint ilOffset)
    {
        ISymUnmanagedMethod? method = TryGetMethod(methodToken);
        if (method is null)
        {
            return new Dictionary<int, string>();
        }

        try
        {
            ThrowIfFailed(
                method.GetRootScope(out ISymUnmanagedScope root),
                "ISymUnmanagedMethod.GetRootScope");
            return ReadLocalScopes(root, ilOffset);
        }
        finally
        {
            DisposeComObject(method);
        }
    }

    /// <summary>
    /// Releases the native reader and its retained symbol and module streams.
    /// </summary>
    public void Dispose()
    {
        ISymUnmanagedReader5? reader = Interlocked.Exchange(ref _reader, null);
        DisposeComObject(reader);
        _pdbStream.Dispose();
        _peReader.Dispose();
    }

    private void ReadMethodSequencePoints(
        uint methodToken,
        List<ManagedSequencePoint> result)
    {
        ISymUnmanagedMethod? method = TryGetMethod(methodToken);
        if (method is null)
        {
            return;
        }

        ISymUnmanagedDocument[] documents = [];
        try
        {
            ThrowIfFailed(
                method.GetSequencePointCount(out int count),
                "ISymUnmanagedMethod.GetSequencePointCount");
            ValidateCount(count, MaximumSequencePointCount, "sequence-point");
            if (count == 0)
            {
                return;
            }

            int[] offsets = new int[count];
            documents = new ISymUnmanagedDocument[count];
            int[] startLines = new int[count];
            int[] startColumns = new int[count];
            int[] endLines = new int[count];
            int[] endColumns = new int[count];
            ThrowIfFailed(
                method.GetSequencePoints(
                    count,
                    out int read,
                    offsets,
                    documents,
                    startLines,
                    startColumns,
                    endLines,
                    endColumns),
                "ISymUnmanagedMethod.GetSequencePoints");
            ValidateReadCount(read, count, "sequence-point");
            for (int index = 0; index < read; index++)
            {
                if (startLines[index] == HiddenSequencePointLine)
                {
                    continue;
                }

                string path = ReadName(documents[index]);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.Add(new ManagedSequencePoint(
                        methodToken,
                        offsets[index],
                        path,
                        startLines[index],
                        startColumns[index],
                        endLines[index],
                        endColumns[index]));
                }
            }
        }
        finally
        {
            foreach (ISymUnmanagedDocument? document in documents
                .Distinct(ReferenceEqualityComparer.Instance))
            {
                DisposeComObject(document);
            }

            DisposeComObject(method);
        }
    }

    private ISymUnmanagedMethod? TryGetMethod(uint methodToken)
    {
        int result = GetReader().GetMethod(checked((int)methodToken), out ISymUnmanagedMethod method);
        if (result < 0)
        {
            return null;
        }

        return method ?? throw new InvalidDataException(
            $"The Windows PDB returned no method for token 0x{methodToken:X8}.");
    }

    private static Dictionary<int, string> ReadLocalScopes(
        ISymUnmanagedScope root,
        uint ilOffset)
    {
        var names = new Dictionary<int, string>();
        var pending = new Stack<ISymUnmanagedScope>();
        pending.Push(root);
        while (pending.TryPop(out ISymUnmanagedScope? scope))
        {
            try
            {
                ThrowIfFailed(
                    scope.GetStartOffset(out int startOffset),
                    "ISymUnmanagedScope.GetStartOffset");
                ThrowIfFailed(
                    scope.GetEndOffset(out int endOffset),
                    "ISymUnmanagedScope.GetEndOffset");
                if (startOffset < 0 || endOffset < startOffset)
                {
                    throw new InvalidDataException(
                        "The Windows PDB returned an invalid local-scope IL range.");
                }

                if (ilOffset >= startOffset && ilOffset < endOffset)
                {
                    ReadLocals(scope, names);
                    PushChildren(scope, pending);
                }
            }
            finally
            {
                DisposeComObject(scope);
            }
        }

        return names;
    }

    private static void ReadLocals(
        ISymUnmanagedScope scope,
        Dictionary<int, string> names)
    {
        ThrowIfFailed(
            scope.GetLocalCount(out int count),
            "ISymUnmanagedScope.GetLocalCount");
        ValidateCount(count, MaximumScopeItemCount, "local");
        if (count == 0)
        {
            return;
        }

        var locals = new ISymUnmanagedVariable[count];
        ThrowIfFailed(
            scope.GetLocals(count, out int read, locals),
            "ISymUnmanagedScope.GetLocals");
        ValidateReadCount(read, count, "local");
        try
        {
            foreach (ISymUnmanagedVariable local in locals.Take(read))
            {
                ThrowIfFailed(
                    local.GetAddressField1(out int slot),
                    "ISymUnmanagedVariable.GetAddressField1");
                names[slot] = ReadName(local);
            }
        }
        finally
        {
            foreach (ISymUnmanagedVariable local in locals.Take(read))
            {
                DisposeComObject(local);
            }
        }
    }

    private static void PushChildren(
        ISymUnmanagedScope scope,
        Stack<ISymUnmanagedScope> pending)
    {
        ThrowIfFailed(
            scope.GetChildren(0, out int count, null!),
            "ISymUnmanagedScope.GetChildren");
        ValidateCount(count, MaximumScopeItemCount, "child scope");
        if (count == 0)
        {
            return;
        }

        var children = new ISymUnmanagedScope[count];
        ThrowIfFailed(
            scope.GetChildren(count, out int read, children),
            "ISymUnmanagedScope.GetChildren");
        ValidateReadCount(read, count, "child scope");
        foreach (ISymUnmanagedScope child in children.Take(read))
        {
            pending.Push(child);
        }
    }

    private static ManagedSymbolDocument ReadDocument(
        ISymUnmanagedDocument document,
        IReadOnlyList<KeyValuePair<string, string>> sourceLinkMappings)
    {
        string path = ReadName(document);
        Guid algorithm = default;
        ThrowIfFailed(
            document.GetChecksumAlgorithmId(ref algorithm),
            "ISymUnmanagedDocument.GetChecksumAlgorithmId");
        DebugSourceChecksum? checksum = ReadChecksum(document, algorithm);
        byte[]? embeddedSource = ReadEmbeddedSource(document);
        if (embeddedSource is not null && checksum is not null &&
            !SourceChecksumVerifier.Matches(embeddedSource, checksum))
        {
            throw new BadImageFormatException(
                $"Embedded source for '{path}' does not match its Windows PDB checksum.");
        }

        return new ManagedSymbolDocument
        {
            Path = path,
            Checksum = checksum,
            EmbeddedSource = embeddedSource,
            SourceLinkUri = ManagedSourceLinkResolver.TryResolve(
                sourceLinkMappings,
                path)
        };
    }

    private static unsafe IReadOnlyList<KeyValuePair<string, string>>
        ReadSourceLinkMappings(ISymUnmanagedReader5 reader)
    {
        ThrowIfFailed(
            reader.GetSourceServerData(out byte* data, out int size),
            "ISymUnmanagedReader4.GetSourceServerData");
        ValidateCount(size, 1024 * 1024, "Source Link byte");
        if (size == 0)
        {
            return [];
        }

        if (data is null)
        {
            throw new InvalidDataException(
                "The Windows PDB returned a null Source Link payload.");
        }

        byte[] sourceLink = new byte[size];
        Marshal.Copy((IntPtr)data, sourceLink, 0, sourceLink.Length);
        int firstContent = 0;
        while (firstContent < sourceLink.Length &&
            sourceLink[firstContent] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            firstContent++;
        }

        return firstContent == sourceLink.Length || sourceLink[firstContent] != '{'
            ? []
            : ManagedSourceLinkResolver.Read(sourceLink);
    }

    private static DebugSourceChecksum? ReadChecksum(
        ISymUnmanagedDocument document,
        Guid algorithm)
    {
        string? name = algorithm == s_sha256Algorithm
            ? "SHA256"
            : algorithm == s_sha1Algorithm ? "SHA1" : null;
        if (name is null)
        {
            return null;
        }

        ThrowIfFailed(
            document.GetChecksum(0, out int count, null!),
            "ISymUnmanagedDocument.GetChecksum");
        ValidateCount(count, 128, "checksum byte");
        byte[] bytes = new byte[count];
        ThrowIfFailed(
            document.GetChecksum(count, out int read, bytes),
            "ISymUnmanagedDocument.GetChecksum");
        ValidateReadCount(read, count, "checksum byte");
        return read == 0
            ? null
            : new DebugSourceChecksum(
                name,
                Convert.ToHexString(bytes.AsSpan(0, read)));
    }

    private static byte[]? ReadEmbeddedSource(ISymUnmanagedDocument document)
    {
        ThrowIfFailed(
            document.HasEmbeddedSource(out bool hasEmbeddedSource),
            "ISymUnmanagedDocument.HasEmbeddedSource");
        if (!hasEmbeddedSource)
        {
            return null;
        }

        ThrowIfFailed(
            document.GetSourceLength(out int length),
            "ISymUnmanagedDocument.GetSourceLength");
        ValidateCount(length, MaximumSourceBytes, "embedded-source byte");
        byte[] blob = new byte[length];
        ThrowIfFailed(
            document.GetSourceRange(
                0,
                0,
                int.MaxValue,
                int.MaxValue,
                length,
                out int read,
                blob),
            "ISymUnmanagedDocument.GetSourceRange");
        ValidateReadCount(read, length, "embedded-source byte");
        byte[] exactBlob = read == blob.Length ? blob : blob[..read];
        return PortablePdbSourceDocumentReader.DecodeEmbeddedSource(exactBlob);
    }

    private static string ReadName(ISymUnmanagedDocument document)
    {
        ThrowIfFailed(
            document.GetUrl(0, out int count, null!),
            "ISymUnmanagedDocument.GetUrl");
        ValidateCount(count, MaximumNameCharacters, "document-name character");
        char[] buffer = new char[count];
        ThrowIfFailed(
            document.GetUrl(count, out int read, buffer),
            "ISymUnmanagedDocument.GetUrl");
        return CreateString(buffer, read, "document name");
    }

    private static string ReadName(ISymUnmanagedVariable variable)
    {
        ThrowIfFailed(
            variable.GetName(0, out int count, null!),
            "ISymUnmanagedVariable.GetName");
        ValidateCount(count, MaximumNameCharacters, "local-name character");
        char[] buffer = new char[count];
        ThrowIfFailed(
            variable.GetName(count, out int read, buffer),
            "ISymUnmanagedVariable.GetName");
        return CreateString(buffer, read, "local name");
    }

    private static string CreateString(char[] buffer, int read, string description)
    {
        ValidateReadCount(read, buffer.Length, $"{description} character");
        int length = read > 0 && buffer[read - 1] == '\0' ? read - 1 : read;
        return new string(buffer, 0, length);
    }

    private ISymUnmanagedReader5 GetReader() => _reader
        ?? throw new ObjectDisposedException(nameof(WindowsPdbReader));

    private static void ValidateCount(int count, int maximum, string description)
    {
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException(
                $"The Windows PDB {description} count {count} is outside the supported range of 0 through {maximum}.");
        }
    }

    private static void ValidateReadCount(int count, int capacity, string description)
    {
        if (count < 0 || count > capacity)
        {
            throw new InvalidDataException(
                $"The Windows PDB returned {count} {description}s for a capacity of {capacity}.");
        }
    }

    private static void ThrowIfFailed(int hresult, string operation)
    {
        if (hresult >= 0)
        {
            return;
        }

        throw new InvalidDataException(
            $"{operation} failed with HRESULT 0x{hresult:X8}.",
            Marshal.GetExceptionForHR(hresult));
    }

    private static void DisposeComObject(object? value)
    {
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
