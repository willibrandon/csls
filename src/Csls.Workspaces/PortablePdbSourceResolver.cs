using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Csls.Workspaces;

/// <summary>
/// Resolves checksum-verified original source through portable PDB and Source Link data.
/// </summary>
internal static class PortablePdbSourceResolver
{
    private const int MaximumPdbLength = 64 * 1024 * 1024;
    private const int MaximumSourceLength = 4 * 1024 * 1024;
    private const int RemoteTimeoutSeconds = 10;
    private static readonly Guid s_embeddedSourceKind = new(
        "0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_sha1Algorithm = new(
        "FF1816EC-AA5E-4D10-87F7-6F4963833460");
    private static readonly Guid s_sha256Algorithm = new(
        "8829D00F-11B8-4213-878B-770E8597AC16");
    private static readonly Guid s_sourceLinkKind = new(
        "CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly Guid s_typeDefinitionDocumentsKind = new(
        "932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");
    private static readonly HttpClient s_httpClient = CreateHttpClient();

    /// <summary>
    /// Resolves candidate source documents that contain one metadata declaration.
    /// </summary>
    /// <param name="project">The project containing the metadata reference.</param>
    /// <param name="symbol">The metadata symbol selected by navigation.</param>
    /// <param name="declarationId">The documentation declaration identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Ordered readable source documents, or an empty list when unavailable.</returns>
    internal static async Task<IReadOnlyList<(string FileName, string Source)>> ResolveAsync(
        Project project,
        ISymbol symbol,
        string declarationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationId);
        try
        {
            Compilation compilation = await project
                .GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Roslyn returned no compilation for project {project.Name}.");
            string? referencePath = FindReferencePath(compilation, symbol);
            INamedTypeSymbol? topLevelType = GetTopLevelType(symbol);
            if (referencePath is null || topLevelType is null)
            {
                return [];
            }

            string? implementationPath = FindImplementationAssemblyPath(
                referencePath,
                topLevelType.ContainingNamespace.ToDisplayString(),
                topLevelType.MetadataName);
            if (implementationPath is null)
            {
                return [];
            }

            EntityHandle? entityHandle = GetImplementationEntityHandle(
                implementationPath,
                declarationId,
                cancellationToken);
            if (entityHandle is null)
            {
                return [];
            }

            IReadOnlyList<(string FileName, string Source)> documents =
                await ResolveDocumentsAsync(
                project,
                implementationPath,
                entityHandle.Value,
                cancellationToken).ConfigureAwait(false);
            return documents.Count > 0
                ? documents
                : await ResolveFrameworkRepositorySourceAsync(
                    implementationPath,
                    topLevelType,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            BadImageFormatException or
            IOException or
            InvalidDataException or
            JsonException or
            UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? FindReferencePath(Compilation compilation, ISymbol symbol)
    {
        IAssemblySymbol containingAssembly = symbol.ContainingAssembly;
        return compilation.References
            .OfType<PortableExecutableReference>()
            .Where(static reference => reference.FilePath is not null)
            .FirstOrDefault(reference =>
                compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                SymbolEqualityComparer.Default.Equals(assembly, containingAssembly))
            ?.FilePath;
    }

    private static INamedTypeSymbol? GetTopLevelType(ISymbol symbol)
    {
        INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        while (type?.ContainingType is INamedTypeSymbol containingType)
        {
            type = containingType;
        }

        return type;
    }

    private static string? FindImplementationAssemblyPath(
        string referencePath,
        string namespaceName,
        string metadataName)
    {
        foreach (string candidate in GetImplementationCandidates(referencePath))
        {
            string? resolved = FollowTypeForwards(candidate, namespaceName, metadataName);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static List<string> GetImplementationCandidates(string referencePath)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        string normalizedPath = referencePath.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        string packsSegment = string.Concat(
            Path.DirectorySeparatorChar,
            "packs",
            Path.DirectorySeparatorChar);
        int packsIndex = normalizedPath.IndexOf(packsSegment, PathComparison);
        if (packsIndex >= 0)
        {
            string dotNetRoot = normalizedPath[..packsIndex];
            string[] segments = normalizedPath[(packsIndex + packsSegment.Length)..].Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 5 &&
                segments[0].EndsWith(".Ref", StringComparison.OrdinalIgnoreCase))
            {
                string sharedName = segments[0][..^".Ref".Length];
                AddIfPresent(Path.Join(
                    dotNetRoot,
                    "shared",
                    sharedName,
                    segments[1],
                    Path.GetFileName(referencePath)));
            }
        }

        string refSegment = string.Concat(
            Path.DirectorySeparatorChar,
            "ref",
            Path.DirectorySeparatorChar);
        int refIndex = normalizedPath.LastIndexOf(refSegment, PathComparison);
        if (refIndex >= 0)
        {
            int targetFrameworkStart = refIndex + refSegment.Length;
            int targetFrameworkEnd = normalizedPath.IndexOf(
                Path.DirectorySeparatorChar,
                targetFrameworkStart);
            if (targetFrameworkEnd > targetFrameworkStart)
            {
                string packageRoot = normalizedPath[..refIndex];
                string targetFramework = normalizedPath[
                    targetFrameworkStart..targetFrameworkEnd];
                AddIfPresent(Path.Join(
                    packageRoot,
                    "lib",
                    targetFramework,
                    Path.GetFileName(referencePath)));
            }
        }

        AddIfPresent(referencePath);

        return candidates;

        void AddIfPresent(string candidate)
        {
            if (File.Exists(candidate))
            {
                string fullPath = Path.GetFullPath(candidate);
                if (seen.Add(fullPath))
                {
                    candidates.Add(fullPath);
                }
            }
        }
    }

    private static string? FollowTypeForwards(
        string assemblyPath,
        string namespaceName,
        string metadataName)
    {
        var visited = new HashSet<string>(PathComparer);
        string currentPath = assemblyPath;
        while (visited.Add(currentPath))
        {
            using var stream = new FileStream(
                currentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            MetadataReader reader = peReader.GetMetadataReader();
            if (FindTypeDefinition(reader, namespaceName, metadataName) is not null)
            {
                return currentPath;
            }

            AssemblyReferenceHandle forwardedAssembly = default;
            foreach (ExportedTypeHandle exportedTypeHandle in reader.ExportedTypes)
            {
                ExportedType exportedType = reader.GetExportedType(exportedTypeHandle);
                if (!string.Equals(
                        reader.GetString(exportedType.Name),
                        metadataName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        reader.GetString(exportedType.Namespace),
                        namespaceName,
                        StringComparison.Ordinal) ||
                    exportedType.Implementation.Kind != HandleKind.AssemblyReference)
                {
                    continue;
                }

                forwardedAssembly = (AssemblyReferenceHandle)exportedType.Implementation;
                break;
            }

            if (forwardedAssembly.IsNil)
            {
                return null;
            }

            string assemblyName = reader.GetString(
                reader.GetAssemblyReference(forwardedAssembly).Name);
            string? directory = Path.GetDirectoryName(currentPath);
            if (directory is null)
            {
                return null;
            }

            string forwardedPath = Path.Join(directory, string.Concat(assemblyName, ".dll"));
            if (!File.Exists(forwardedPath))
            {
                return null;
            }

            currentPath = forwardedPath;
        }

        return null;
    }

    private static TypeDefinitionHandle? FindTypeDefinition(
        MetadataReader reader,
        string namespaceName,
        string metadataName)
    {
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            if (string.Equals(
                    reader.GetString(definition.Name),
                    metadataName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    reader.GetString(definition.Namespace),
                    namespaceName,
                    StringComparison.Ordinal))
            {
                return handle;
            }
        }

        return null;
    }

    private static EntityHandle? GetImplementationEntityHandle(
        string implementationPath,
        string declarationId,
        CancellationToken cancellationToken)
    {
        MetadataReference reference = MetadataReference.CreateFromFile(implementationPath);
        var compilation = CSharpCompilation.Create(
            "csls.metadata.source",
            references: [reference]);
        ISymbol? implementationSymbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(
            declarationId,
            compilation);
        if (implementationSymbol is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return MetadataTokens.EntityHandle(implementationSymbol.MetadataToken);
    }

    private static async Task<IReadOnlyList<(string FileName, string Source)>>
        ResolveDocumentsAsync(
        Project project,
        string assemblyPath,
        EntityHandle entityHandle,
        CancellationToken cancellationToken)
    {
        using var assemblyStream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using var peReader = new PEReader(assemblyStream, PEStreamOptions.PrefetchEntireImage);
        if (!peReader.HasMetadata)
        {
            return [];
        }

        if (peReader.TryOpenAssociatedPortablePdb(
            assemblyPath,
            OpenFileIfPresent,
            out MetadataReaderProvider? associatedProvider,
            out _))
        {
            using (associatedProvider)
            {
                return await ResolveDocumentsAsync(
                    project,
                    peReader.GetMetadataReader(),
                    associatedProvider!.GetMetadataReader(),
                    entityHandle,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        string? downloadedPdb = await DownloadPdbAsync(
            peReader,
            cancellationToken).ConfigureAwait(false);
        if (downloadedPdb is null)
        {
            return [];
        }

        using var pdbStream = new FileStream(
            downloadedPdb,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(
            pdbStream);
        return await ResolveDocumentsAsync(
            project,
            peReader.GetMetadataReader(),
            provider.GetMetadataReader(),
            entityHandle,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<(string FileName, string Source)>>
        ResolveFrameworkRepositorySourceAsync(
        string implementationPath,
        INamedTypeSymbol topLevelType,
        CancellationToken cancellationToken)
    {
        string normalizedPath = implementationPath.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        string sharedFrameworkSegment = string.Concat(
            Path.DirectorySeparatorChar,
            "shared",
            Path.DirectorySeparatorChar,
            "Microsoft.NETCore.App",
            Path.DirectorySeparatorChar);
        if (!normalizedPath.Contains(sharedFrameworkSegment, PathComparison))
        {
            return [];
        }

        string productVersion = FileVersionInfo.GetVersionInfo(implementationPath)
            .ProductVersion ?? string.Empty;
        int commitSeparator = productVersion.LastIndexOf('+');
        string commit = commitSeparator < 0
            ? string.Empty
            : productVersion[(commitSeparator + 1)..];
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
        {
            return [];
        }

        string assemblyName = Path.GetFileNameWithoutExtension(implementationPath);
        string typeName = topLevelType.MetadataName.Split('`')[0];
        string namespacePath = topLevelType.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : topLevelType.ContainingNamespace.ToDisplayString().Replace('.', '/');
        string relativePath = string.Join(
            '/',
            new[]
            {
                "src",
                "libraries",
                assemblyName,
                "src",
                namespacePath,
                string.Concat(typeName, ".cs")
            }.Where(static component => component.Length > 0));
        foreach (string candidate in new[]
        {
            string.Concat(
                "https://raw.githubusercontent.com/dotnet/dotnet/",
                commit,
                "/src/runtime/",
                relativePath),
            string.Concat(
                "https://raw.githubusercontent.com/dotnet/runtime/",
                commit,
                '/',
                relativePath)
        })
        {
            byte[]? source = await DownloadTrustedSourceAsync(
                new Uri(candidate),
                cancellationToken).ConfigureAwait(false);
            if (source is not null)
            {
                return [(string.Concat(typeName, ".cs"), DecodeSource(source))];
            }
        }

        return [];
    }

    private static async Task<IReadOnlyList<(string FileName, string Source)>>
        ResolveDocumentsAsync(
        Project project,
        MetadataReader assemblyReader,
        MetadataReader pdbReader,
        EntityHandle entityHandle,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentHandle> documentHandles = FindDocumentHandles(
            entityHandle,
            assemblyReader,
            pdbReader);
        string? sourceLinkJson = GetSourceLinkJson(pdbReader);
        var result = new List<(string FileName, string Source)>(documentHandles.Count);
        foreach (DocumentHandle handle in documentHandles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            System.Reflection.Metadata.Document document = pdbReader.GetDocument(handle);
            string documentPath = pdbReader.GetString(document.Name);
            byte[] checksum = pdbReader.GetBlobBytes(document.Hash);
            Guid checksumAlgorithm = pdbReader.GetGuid(document.HashAlgorithm);
            byte[]? source = TryGetEmbeddedSource(pdbReader, handle);
            if (source is not null &&
                !HasExpectedChecksum(source, checksumAlgorithm, checksum))
            {
                source = null;
            }

            source ??= await TryReadFileAsync(
                documentPath,
                checksumAlgorithm,
                checksum,
                cancellationToken).ConfigureAwait(false);
            string? sourceLinkUrl = TryResolveSourceLinkUrl(
                sourceLinkJson,
                documentPath);
            if (source is null && sourceLinkUrl is not null)
            {
                source = await TryReadSiblingRepositorySourceAsync(
                    project,
                    sourceLinkUrl,
                    checksumAlgorithm,
                    checksum,
                    cancellationToken).ConfigureAwait(false);
            }

            if (source is null && sourceLinkUrl is not null)
            {
                source = await DownloadSourceAsync(
                    sourceLinkUrl,
                    checksumAlgorithm,
                    checksum,
                    cancellationToken).ConfigureAwait(false);
            }

            if (source is null)
            {
                continue;
            }

            string text = DecodeSource(source);
            if (text.Length <= MaximumSourceLength)
            {
                result.Add((GetSafeFileName(documentPath, "source.cs"), text));
            }
        }

        return result;
    }

    private static IReadOnlyList<DocumentHandle> FindDocumentHandles(
        EntityHandle handle,
        MetadataReader assemblyReader,
        MetadataReader pdbReader)
    {
        var documents = new HashSet<DocumentHandle>();
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                AddMethodDocuments(
                    (MethodDefinitionHandle)handle,
                    assemblyReader,
                    pdbReader,
                    documents,
                    includeDeclaringType: true);
                break;
            case HandleKind.TypeDefinition:
                AddTypeDocuments(
                    (TypeDefinitionHandle)handle,
                    assemblyReader,
                    pdbReader,
                    documents);
                break;
            case HandleKind.FieldDefinition:
                AddTypeDocuments(
                    assemblyReader.GetFieldDefinition((FieldDefinitionHandle)handle)
                        .GetDeclaringType(),
                    assemblyReader,
                    pdbReader,
                    documents);
                break;
            case HandleKind.PropertyDefinition:
                PropertyAccessors propertyAccessors = assemblyReader
                    .GetPropertyDefinition((PropertyDefinitionHandle)handle)
                    .GetAccessors();
                AddAccessorDocuments(
                    propertyAccessors.Getter,
                    propertyAccessors.Setter,
                    propertyAccessors.Others,
                    assemblyReader,
                    pdbReader,
                    documents);
                break;
            case HandleKind.EventDefinition:
                EventAccessors eventAccessors = assemblyReader
                    .GetEventDefinition((EventDefinitionHandle)handle)
                    .GetAccessors();
                AddAccessorDocuments(
                    eventAccessors.Adder,
                    eventAccessors.Remover,
                    eventAccessors.Others,
                    assemblyReader,
                    pdbReader,
                    documents);
                if (!eventAccessors.Raiser.IsNil)
                {
                    AddMethodDocuments(
                        eventAccessors.Raiser,
                        assemblyReader,
                        pdbReader,
                        documents,
                        includeDeclaringType: true);
                }

                break;
        }

        return [.. documents.OrderBy(static document => MetadataTokens.GetRowNumber(document))];
    }

    private static void AddAccessorDocuments(
        MethodDefinitionHandle first,
        MethodDefinitionHandle second,
        ImmutableArray<MethodDefinitionHandle> others,
        MetadataReader assemblyReader,
        MetadataReader pdbReader,
        HashSet<DocumentHandle> documents)
    {
        if (!first.IsNil)
        {
            AddMethodDocuments(
                first,
                assemblyReader,
                pdbReader,
                documents,
                includeDeclaringType: true);
        }

        if (!second.IsNil)
        {
            AddMethodDocuments(
                second,
                assemblyReader,
                pdbReader,
                documents,
                includeDeclaringType: true);
        }

        foreach (MethodDefinitionHandle other in others)
        {
            AddMethodDocuments(
                other,
                assemblyReader,
                pdbReader,
                documents,
                includeDeclaringType: true);
        }
    }

    private static void AddMethodDocuments(
        MethodDefinitionHandle handle,
        MetadataReader assemblyReader,
        MetadataReader pdbReader,
        HashSet<DocumentHandle> documents,
        bool includeDeclaringType)
    {
        MethodDebugInformation information = pdbReader.GetMethodDebugInformation(handle);
        if (!information.Document.IsNil)
        {
            documents.Add(information.Document);
            return;
        }

        if (!information.SequencePointsBlob.IsNil)
        {
            foreach (SequencePoint point in information.GetSequencePoints()
                .Where(static point => !point.Document.IsNil))
            {
                documents.Add(point.Document);
                includeDeclaringType = false;
            }
        }

        if (includeDeclaringType)
        {
            AddTypeDocuments(
                assemblyReader.GetMethodDefinition(handle).GetDeclaringType(),
                assemblyReader,
                pdbReader,
                documents);
        }
    }

    private static void AddTypeDocuments(
        TypeDefinitionHandle handle,
        MetadataReader assemblyReader,
        MetadataReader pdbReader,
        HashSet<DocumentHandle> documents,
        bool includeContainingType = true)
    {
        foreach (CustomDebugInformation information in
            pdbReader.GetCustomDebugInformation(handle).Select(
                pdbReader.GetCustomDebugInformation))
        {
            if (pdbReader.GetGuid(information.Kind) != s_typeDefinitionDocumentsKind)
            {
                continue;
            }

            BlobReader blobReader = pdbReader.GetBlobReader(information.Value);
            while (blobReader.RemainingBytes > 0)
            {
                documents.Add(MetadataTokens.DocumentHandle(
                    blobReader.ReadCompressedInteger()));
            }
        }

        TypeDefinition definition = assemblyReader.GetTypeDefinition(handle);
        foreach (MethodDefinitionHandle method in definition.GetMethods())
        {
            AddMethodDocuments(
                method,
                assemblyReader,
                pdbReader,
                documents,
                includeDeclaringType: false);
        }

        if (includeContainingType && definition.IsNested)
        {
            AddTypeDocuments(
                definition.GetDeclaringType(),
                assemblyReader,
                pdbReader,
                documents);
        }

        foreach (TypeDefinitionHandle nestedType in definition.GetNestedTypes())
        {
            AddTypeDocuments(
                nestedType,
                assemblyReader,
                pdbReader,
                documents,
                includeContainingType: false);
        }
    }

    private static string? GetSourceLinkJson(MetadataReader pdbReader)
    {
        foreach (CustomDebugInformationHandle handle in pdbReader.GetCustomDebugInformation(
            EntityHandle.ModuleDefinition))
        {
            CustomDebugInformation information = pdbReader.GetCustomDebugInformation(handle);
            if (pdbReader.GetGuid(information.Kind) == s_sourceLinkKind)
            {
                return Encoding.UTF8.GetString(pdbReader.GetBlobBytes(information.Value));
            }
        }

        return null;
    }

    private static byte[]? TryGetEmbeddedSource(
        MetadataReader pdbReader,
        DocumentHandle documentHandle)
    {
        foreach (CustomDebugInformationHandle handle in pdbReader.GetCustomDebugInformation(
            documentHandle))
        {
            CustomDebugInformation information = pdbReader.GetCustomDebugInformation(handle);
            if (pdbReader.GetGuid(information.Kind) != s_embeddedSourceKind)
            {
                continue;
            }

            byte[] embedded = pdbReader.GetBlobBytes(information.Value);
            if (embedded.Length < sizeof(int))
            {
                return null;
            }

            int uncompressedLength = BitConverter.ToInt32(embedded, 0);
            if (uncompressedLength == 0)
            {
                return embedded[sizeof(int)..];
            }

            if (uncompressedLength < 0 || uncompressedLength > MaximumSourceLength)
            {
                return null;
            }

            using var compressed = new MemoryStream(
                embedded,
                sizeof(int),
                embedded.Length - sizeof(int));
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var source = new MemoryStream(uncompressedLength);
            deflate.CopyTo(source);
            return source.Length == uncompressedLength ? source.ToArray() : null;
        }

        return null;
    }

    private static string? TryResolveSourceLinkUrl(string? sourceLinkJson, string documentPath)
    {
        if (sourceLinkJson is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(sourceLinkJson);
        if (!document.RootElement.TryGetProperty("documents", out JsonElement mappings) ||
            mappings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (JsonProperty mapping in mappings.EnumerateObject())
        {
            string replacement = mapping.Value.GetString() ?? string.Empty;
            int wildcard = mapping.Name.IndexOf('*', StringComparison.Ordinal);
            if (wildcard < 0)
            {
                if (string.Equals(mapping.Name, documentPath, StringComparison.Ordinal))
                {
                    return replacement;
                }

                continue;
            }

            string prefix = mapping.Name[..wildcard];
            string suffix = mapping.Name[(wildcard + 1)..];
            if (!documentPath.StartsWith(prefix, StringComparison.Ordinal) ||
                !documentPath.EndsWith(suffix, StringComparison.Ordinal) ||
                documentPath.Length < prefix.Length + suffix.Length)
            {
                continue;
            }

            string value = documentPath[
                prefix.Length..(documentPath.Length - suffix.Length)];
            return replacement.Replace("*", value, StringComparison.Ordinal);
        }

        return null;
    }

    private static async Task<byte[]?> TryReadSiblingRepositorySourceAsync(
        Project project,
        string sourceLinkUrl,
        Guid checksumAlgorithm,
        byte[] checksum,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceLinkUrl, UriKind.Absolute, out Uri? uri) ||
            !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || string.IsNullOrWhiteSpace(project.FilePath))
        {
            return null;
        }

        string? repositoryRoot = FindRepositoryRoot(Path.GetDirectoryName(project.FilePath));
        string? sourceRoot = repositoryRoot is null
            ? null
            : Path.GetDirectoryName(repositoryRoot);
        if (sourceRoot is null)
        {
            return null;
        }

        string[] candidateSegments =
        [
            Uri.UnescapeDataString(segments[1]),
            .. segments.Skip(3).Select(Uri.UnescapeDataString)
        ];
        if (candidateSegments.Any(static segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Contains('/', StringComparison.Ordinal) ||
                segment.Contains('\\', StringComparison.Ordinal) ||
                segment.Contains('\0', StringComparison.Ordinal)))
        {
            return null;
        }

        string candidate = Path.GetFullPath(Path.Join(sourceRoot, Path.Join(candidateSegments)));
        string sourceRootPrefix = string.Concat(
            Path.GetFullPath(sourceRoot),
            Path.DirectorySeparatorChar);
        if (!candidate.StartsWith(sourceRootPrefix, PathComparison))
        {
            return null;
        }

        return await TryReadFileAsync(
            candidate,
            checksumAlgorithm,
            checksum,
            cancellationToken).ConfigureAwait(false);
    }

    private static string? FindRepositoryRoot(string? startPath)
    {
        for (DirectoryInfo? directory = startPath is null
                ? null
                : new DirectoryInfo(startPath);
            directory is not null;
            directory = directory.Parent)
        {
            if (Directory.Exists(Path.Join(directory.FullName, ".git")) ||
                File.Exists(Path.Join(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static async Task<byte[]?> TryReadFileAsync(
        string path,
        Guid checksumAlgorithm,
        byte[] checksum,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var information = new FileInfo(path);
        if (information.Length > MaximumSourceLength)
        {
            return null;
        }

        byte[] source = await File.ReadAllBytesAsync(path, cancellationToken)
            .ConfigureAwait(false);
        return HasExpectedChecksum(source, checksumAlgorithm, checksum) ? source : null;
    }

    private static async Task<byte[]?> DownloadSourceAsync(
        string sourceLinkUrl,
        Guid checksumAlgorithm,
        byte[] checksum,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceLinkUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        string cachePath = Path.Join(
            GetCacheRoot(),
            "sources",
            Convert.ToHexString(checksum),
            GetSafeFileName(Path.GetFileName(uri.AbsolutePath), "source.cs"));
        byte[]? cached = await TryReadFileAsync(
            cachePath,
            checksumAlgorithm,
            checksum,
            cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        byte[]? downloaded = await DownloadBytesAsync(
            uri,
            MaximumSourceLength,
            cancellationToken).ConfigureAwait(false);
        if (downloaded is null ||
            !HasExpectedChecksum(downloaded, checksumAlgorithm, checksum))
        {
            return null;
        }

        await WriteCacheFileAsync(cachePath, downloaded, cancellationToken)
            .ConfigureAwait(false);
        return downloaded;
    }

    private static async Task<byte[]?> DownloadTrustedSourceAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        string identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        string cachePath = Path.Join(
            GetCacheRoot(),
            "framework-sources",
            identity,
            GetSafeFileName(Path.GetFileName(uri.AbsolutePath), "source.cs"));
        if (File.Exists(cachePath))
        {
            return await File.ReadAllBytesAsync(cachePath, cancellationToken)
                .ConfigureAwait(false);
        }

        byte[]? source = await DownloadBytesAsync(
            uri,
            MaximumSourceLength,
            cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        await WriteCacheFileAsync(cachePath, source, cancellationToken)
            .ConfigureAwait(false);
        return source;
    }

    private static bool HasExpectedChecksum(
        byte[] source,
        Guid checksumAlgorithm,
        byte[] checksum)
    {
        byte[] actual;
        if (checksumAlgorithm == s_sha256Algorithm)
        {
            actual = SHA256.HashData(source);
        }
        else if (checksumAlgorithm == s_sha1Algorithm)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(source);
            actual = hash.GetHashAndReset();
        }
        else
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, checksum);
    }

    private static string DecodeSource(byte[] source)
    {
        using var stream = new MemoryStream(source, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static async Task<string?> DownloadPdbAsync(
        PEReader peReader,
        CancellationToken cancellationToken)
    {
        foreach (DebugDirectoryEntry entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type != DebugDirectoryEntryType.CodeView)
            {
                continue;
            }

            CodeViewDebugDirectoryData codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
            string pdbName = GetSafeFileName(Path.GetFileName(codeView.Path), "symbols.pdb");
            string key = string.Concat(
                codeView.Guid.ToString("N").ToUpperInvariant(),
                codeView.Age.ToString("X", CultureInfo.InvariantCulture));
            string cachePath = Path.Join(GetCacheRoot(), "symbols", pdbName, key, pdbName);
            if (File.Exists(cachePath))
            {
                return await IsPortablePdbAsync(cachePath, cancellationToken)
                    .ConfigureAwait(false)
                    ? cachePath
                    : null;
            }

            IEnumerable<string> symbolEndpoints =
            [
                "https://msdl.microsoft.com/download/symbols",
                "https://symbols.nuget.org/download/symbols"
            ];
            foreach (Uri uri in symbolEndpoints.Select(endpoint => new Uri(string.Concat(
                    endpoint,
                    '/',
                    Uri.EscapeDataString(pdbName),
                    '/',
                    key,
                    '/',
                    Uri.EscapeDataString(pdbName)))))
            {
                byte[]? pdb = await DownloadBytesAsync(
                    uri,
                    MaximumPdbLength,
                    cancellationToken).ConfigureAwait(false);
                if (pdb is null)
                {
                    continue;
                }

                await WriteCacheFileAsync(cachePath, pdb, cancellationToken)
                    .ConfigureAwait(false);
                if (IsPortablePdb(pdb))
                {
                    return cachePath;
                }
            }
        }

        return null;
    }

    private static async Task<bool> IsPortablePdbAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] signature = new byte[4];
        int read = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
        return read == signature.Length && IsPortablePdb(signature);
    }

    private static bool IsPortablePdb(ReadOnlySpan<byte> content) =>
        content.StartsWith("BSJB"u8);

    private static async Task<byte[]?> DownloadBytesAsync(
        Uri uri,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(RemoteTimeoutSeconds));
        try
        {
            using HttpResponseMessage response = await s_httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound ||
                !response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > maximumLength)
            {
                return null;
            }

            Stream responseStream = await response.Content
                .ReadAsStreamAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable responseStreamCleanup = responseStream
                .ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];
            while (true)
            {
                int read = await responseStream.ReadAsync(
                    chunk,
                    timeoutSource.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumLength)
                {
                    return null;
                }

                await buffer.WriteAsync(
                    chunk.AsMemory(0, read),
                    timeoutSource.Token).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task WriteCacheFileAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static string GetCacheRoot()
    {
        string localDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localDataPath))
        {
            localDataPath = Path.GetTempPath();
        }

        return Path.Join(localDataPath, "csls", "source-navigation");
    }

    private static string GetSafeFileName(string name, string fallback)
    {
        string fileName = Path.GetFileName(name.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
    }

    private static FileStream? OpenFileIfPresent(string path) =>
        File.Exists(path)
            ? new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete)
            : null;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("csls-source-navigation/1.0");
        return client;
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

}
