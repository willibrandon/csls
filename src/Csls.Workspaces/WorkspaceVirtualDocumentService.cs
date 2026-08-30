using Csls.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using LspLocation = Csls.Protocol.Location;
using LspRange = Csls.Protocol.Range;

namespace Csls.Workspaces;

/// <summary>
/// Resolves generated documents and produces C# declarations from Roslyn metadata symbols.
/// </summary>
internal static class WorkspaceVirtualDocumentService
{
    private const int MaximumSourceLength = 4 * 1024 * 1024;
    private const int MaximumCachedDocumentsPerProject = 128;
    private static readonly ConditionalWeakTable<
        Project,
        ConcurrentDictionary<string, (CSharpMetadataResponse Response, LspRange Range)>>
        s_metadataCache = [];
    private static readonly SymbolDisplayFormat s_symbolNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType);

    /// <summary>
    /// Resolves one self-describing virtual URI against the current loaded projects.
    /// </summary>
    /// <param name="projects">The immutable loaded project snapshots.</param>
    /// <param name="uri">The generated or metadata-backed document URI.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The virtual document response, when the URI resolves.</returns>
    internal static async Task<CSharpMetadataResponse?> GetAsync(
        IEnumerable<Project> projects,
        DocumentUri uri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (VirtualDocumentUri.TryParseGenerated(
            uri,
            out string generatedProjectPath,
            out string hintName))
        {
            Project? project = FindProject(projects, generatedProjectPath);
            return project is null
                ? null
                : await GetGeneratedAsync(project, hintName, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (VirtualDocumentUri.TryParseMetadata(
            uri,
            out string metadataProjectPath,
            out string declarationId))
        {
            Project? project = FindProject(projects, metadataProjectPath);
            if (project is null)
            {
                return null;
            }

            (CSharpMetadataResponse Response, LspRange Range)? metadata =
                await CreateMetadataAsync(project, declarationId, cancellationToken)
                    .ConfigureAwait(false);
            return metadata?.Response;
        }

        return null;
    }

    /// <summary>
    /// Replaces virtual navigation locations with readable cached source files.
    /// </summary>
    /// <param name="projects">The immutable loaded project snapshots.</param>
    /// <param name="locations">The ordered navigation locations to adapt.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Locations that use file URIs whenever virtual source was resolved.</returns>
    internal static async Task<IReadOnlyList<LspLocation>> MaterializeLocationsAsync(
        IEnumerable<Project> projects,
        IReadOnlyList<LspLocation> locations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(locations);
        Project[] projectSnapshots = [.. projects];
        var materializedLocations = new List<LspLocation>(locations.Count);
        foreach (LspLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!VirtualDocumentUri.TryParseGenerated(
                    location.Uri,
                    out _,
                    out _) &&
                !VirtualDocumentUri.TryParseMetadata(
                    location.Uri,
                    out _,
                    out _))
            {
                materializedLocations.Add(location);
                continue;
            }

            CSharpMetadataResponse? document = await GetAsync(
                projectSnapshots,
                location.Uri,
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                materializedLocations.Add(location);
                continue;
            }

            string path = await WriteMaterializedDocumentAsync(
                location.Uri,
                document.Source,
                document.DocumentName,
                cancellationToken).ConfigureAwait(false);
            materializedLocations.Add(new LspLocation
            {
                Uri = DocumentUri.FromFileSystemPath(path),
                Range = location.Range
            });
        }

        return materializedLocations;
    }

    /// <summary>
    /// Creates an exact navigation target for one metadata symbol.
    /// </summary>
    /// <param name="project">The requesting project snapshot.</param>
    /// <param name="symbol">The metadata symbol.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The virtual location, when source can be produced.</returns>
    internal static async Task<LspLocation?> GetMetadataLocationAsync(
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(symbol);
        if (string.IsNullOrWhiteSpace(project.FilePath))
        {
            return null;
        }

        string? declarationId = DocumentationCommentId.CreateDeclarationId(
            symbol.OriginalDefinition);
        if (string.IsNullOrWhiteSpace(declarationId))
        {
            return null;
        }

        (CSharpMetadataResponse Response, LspRange Range)? metadata =
            await CreateMetadataAsync(project, declarationId, cancellationToken)
                .ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        DocumentUri? uri = VirtualDocumentUri.CreateMetadata(
            project.FilePath,
            symbol,
            metadata.Value.Response.DocumentName);
        return uri is null
            ? null
            : new LspLocation
            {
                Uri = uri.Value,
                Range = metadata.Value.Range
            };
    }

    private static Project? FindProject(IEnumerable<Project> projects, string projectFilePath) =>
        projects.FirstOrDefault(project =>
            string.Equals(project.FilePath, projectFilePath, PathComparison));

    private static async Task<string> WriteMaterializedDocumentAsync(
        DocumentUri uri,
        string source,
        string? documentName,
        CancellationToken cancellationToken)
    {
        string localDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localDataPath))
        {
            localDataPath = Path.GetTempPath();
        }

        string directory = Path.Join(localDataPath, "csls", "virtual-documents");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        string identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(uri.ToString())));
        directory = Path.Join(directory, identity);
        Directory.CreateDirectory(directory);
        string readableName = GetReadableFileName(documentName);
        string path = Path.Join(directory, readableName);
        if (File.Exists(path) && string.Equals(
            await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
            source,
            StringComparison.Ordinal))
        {
            return path;
        }

        string temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                source,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return path;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task<CSharpMetadataResponse?> GetGeneratedAsync(
        Project project,
        string hintName,
        CancellationToken cancellationToken)
    {
        IEnumerable<SourceGeneratedDocument> documents = await project
            .GetSourceGeneratedDocumentsAsync(cancellationToken)
            .ConfigureAwait(false);
        SourceGeneratedDocument? document = documents.FirstOrDefault(candidate =>
            string.Equals(candidate.HintName, hintName, StringComparison.Ordinal));
        if (document is null)
        {
            return null;
        }

        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text.Length > MaximumSourceLength)
        {
            throw new InvalidOperationException(
                $"Generated document {hintName} exceeds {MaximumSourceLength} characters.");
        }

        return new CSharpMetadataResponse
        {
            ProjectName = project.Name,
            AssemblyName = project.AssemblyName,
            DocumentName = document.HintName,
            SymbolName = hintName,
            Source = text.ToString()
        };
    }

    private static async Task<(CSharpMetadataResponse Response, LspRange Range)?>
        CreateMetadataAsync(
        Project project,
        string declarationId,
        CancellationToken cancellationToken)
    {
        ConcurrentDictionary<string, (CSharpMetadataResponse Response, LspRange Range)> cache =
            s_metadataCache.GetValue(
                project,
                static _ => new ConcurrentDictionary<
                    string,
                    (CSharpMetadataResponse Response, LspRange Range)>(StringComparer.Ordinal));
        if (cache.TryGetValue(
            declarationId,
            out (CSharpMetadataResponse Response, LspRange Range) cached))
        {
            return cached;
        }

        Compilation compilation = await project
            .GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Roslyn returned no compilation for project {project.Name}.");
        ISymbol? symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(
            declarationId,
            compilation);
        if (symbol is null || symbol.ContainingAssembly is null)
        {
            return null;
        }

        INamedTypeSymbol? topLevelType = GetTopLevelType(symbol);
        if (topLevelType is null)
        {
            return null;
        }

        IReadOnlyList<(string FileName, string Source)> sourceDocuments =
            await PortablePdbSourceResolver.ResolveAsync(
                project,
                symbol,
                declarationId,
                cancellationToken).ConfigureAwait(false);
        foreach ((string fileName, string sourceText) in sourceDocuments)
        {
            Document sourceDocument = project.AddDocument(fileName, sourceText);
            LspRange? sourceRange = await TryFindDeclarationRangeAsync(
                sourceDocument,
                declarationId,
                cancellationToken).ConfigureAwait(false);
            if (sourceRange is null)
            {
                continue;
            }

            (CSharpMetadataResponse Response, LspRange Range) sourceResult = (
                new CSharpMetadataResponse
                {
                    ProjectName = project.Name,
                    AssemblyName = symbol.ContainingAssembly.Name,
                    DocumentName = fileName,
                    SymbolName = symbol.ToDisplayString(s_symbolNameFormat),
                    Source = sourceText
                },
                sourceRange.Value);
            if (cache.Count < MaximumCachedDocumentsPerProject)
            {
                cache.TryAdd(declarationId, sourceResult);
            }

            return sourceResult;
        }

        Document document = await CreateMetadataDocumentAsync(
            project,
            topLevelType,
            cancellationToken).ConfigureAwait(false);
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        string source = string.Concat(text.ToString(), "\n");
        if (source.Length > MaximumSourceLength)
        {
            throw new InvalidOperationException(
                $"Metadata document {declarationId} exceeds {MaximumSourceLength} characters.");
        }

        LspRange range = await TryFindDeclarationRangeAsync(
            document,
            declarationId,
            cancellationToken).ConfigureAwait(false)
            ?? new LspRange(new Position(0, 0), new Position(0, 0));
        (CSharpMetadataResponse Response, LspRange Range) result = (
            new CSharpMetadataResponse
            {
                ProjectName = project.Name,
                AssemblyName = symbol.ContainingAssembly.Name,
                DocumentName = string.Concat(topLevelType.Name, ".cs"),
                SymbolName = symbol.ToDisplayString(s_symbolNameFormat),
                Source = source
            },
            range);
        if (cache.Count < MaximumCachedDocumentsPerProject)
        {
            cache.TryAdd(declarationId, result);
        }

        return result;
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

    private static async Task<Document> CreateMetadataDocumentAsync(
        Project project,
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
    {
        var generator = SyntaxGenerator.GetGenerator(project);
        SyntaxNode declaration = generator.Declaration(type);
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            declaration = generator.NamespaceDeclaration(
                type.ContainingNamespace.ToDisplayString(),
                declaration);
        }

        SyntaxNode root = generator.CompilationUnit(declaration).NormalizeWhitespace(
            indentation: "    ",
            eol: "\n",
            elasticTrivia: false);
        root = root.WithLeadingTrivia(SyntaxFactory.ParseLeadingTrivia("#nullable enable\n\n"));
        Document document = project.AddDocument("csls.metadata.cs", root);
        document = await Simplifier.ReduceAsync(
            document,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        document = await Formatter.FormatAsync(
            document,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return document;
    }

    private static async Task<LspRange?> TryFindDeclarationRangeAsync(
        Document document,
        string declarationId,
        CancellationToken cancellationToken)
    {
        SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no metadata syntax root.");
        SemanticModel semanticModel = await document
            .GetSemanticModelAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn returned no metadata semantic model.");
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        foreach (SyntaxNode node in root.DescendantNodesAndSelf())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ISymbol? declaredSymbol = semanticModel.GetDeclaredSymbol(node, cancellationToken);
            if (declaredSymbol is null ||
                !string.Equals(
                    DocumentationCommentId.CreateDeclarationId(declaredSymbol.OriginalDefinition),
                    declarationId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string identifier = declaredSymbol is IMethodSymbol
            {
                MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor
            }
                ? declaredSymbol.ContainingType.Name
                : declaredSymbol.Name;
            SyntaxToken token = node
                .DescendantTokens()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.ValueText, identifier, StringComparison.Ordinal));
            TextSpan span = token.RawKind == 0 ? node.Span : token.Span;
            LinePositionSpan lineSpan = text.Lines.GetLinePositionSpan(span);
            return new LspRange(
                new Position(lineSpan.Start.Line, lineSpan.Start.Character),
                new Position(lineSpan.End.Line, lineSpan.End.Character));
        }

        return null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string GetReadableFileName(string? documentName)
    {
        string candidate = Path.GetFileName(
            (documentName ?? string.Empty).Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "..")
        {
            return "metadata.cs";
        }

        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(candidate.Select(character =>
            character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ||
            char.IsControl(character) ||
            invalidCharacters.Contains(character)
                ? '_'
                : character));
    }
}
