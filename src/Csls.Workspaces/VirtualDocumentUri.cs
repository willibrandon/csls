using Csls.Protocol;
using Microsoft.CodeAnalysis;
using System.Buffers.Text;
using System.Text;

namespace Csls.Workspaces;

/// <summary>
/// Creates and parses self-describing URIs for generated and metadata-backed documents.
/// </summary>
internal static class VirtualDocumentUri
{
    private const int MaximumComponentLength = 8_192;
    private const string GeneratedPrefix = "csharp:/generated/";
    private const string MetadataPrefix = "csharp:/metadata/";
    private const string SourceSuffix = ".cs";
    private static readonly UTF8Encoding s_utf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Creates a stable URI for one source-generated document.
    /// </summary>
    /// <param name="projectFilePath">The owning project file path.</param>
    /// <param name="hintName">The generator-provided hint name.</param>
    /// <returns>The virtual document URI.</returns>
    internal static DocumentUri CreateGenerated(string projectFilePath, string hintName) =>
        Create(GeneratedPrefix, projectFilePath, hintName, suffix: string.Empty);

    /// <summary>
    /// Creates a stable URI for one metadata symbol when it has a declaration identifier.
    /// </summary>
    /// <param name="projectFilePath">The requesting project file path.</param>
    /// <param name="symbol">The metadata symbol.</param>
    /// <returns>The virtual document URI, or <see langword="null"/> when unsupported.</returns>
    internal static DocumentUri? CreateMetadata(string projectFilePath, ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        string? declarationId = DocumentationCommentId.CreateDeclarationId(
            symbol.OriginalDefinition);
        return string.IsNullOrWhiteSpace(declarationId)
            ? null
            : Create(MetadataPrefix, projectFilePath, declarationId, SourceSuffix);
    }

    /// <summary>
    /// Parses a source-generated document URI into its project path and hint name.
    /// </summary>
    /// <param name="uri">The virtual document URI.</param>
    /// <param name="projectFilePath">The decoded project file path.</param>
    /// <param name="hintName">The decoded generator hint name.</param>
    /// <returns><see langword="true"/> when the URI is valid.</returns>
    internal static bool TryParseGenerated(
        DocumentUri uri,
        out string projectFilePath,
        out string hintName) =>
        TryParse(uri, GeneratedPrefix, suffix: string.Empty, out projectFilePath, out hintName);

    /// <summary>
    /// Parses a metadata document URI into its project path and declaration identifier.
    /// </summary>
    /// <param name="uri">The virtual document URI.</param>
    /// <param name="projectFilePath">The decoded project file path.</param>
    /// <param name="declarationId">The decoded symbol declaration identifier.</param>
    /// <returns><see langword="true"/> when the URI is valid.</returns>
    internal static bool TryParseMetadata(
        DocumentUri uri,
        out string projectFilePath,
        out string declarationId) =>
        TryParse(uri, MetadataPrefix, SourceSuffix, out projectFilePath, out declarationId);

    private static DocumentUri Create(
        string prefix,
        string projectFilePath,
        string value,
        string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string projectUri = DocumentUri.FromFileSystemPath(projectFilePath).ToString();
        return DocumentUri.Parse(
            string.Concat(
                prefix,
                EncodeComponent(projectUri),
                '/',
                EncodeComponent(value),
                suffix));
    }

    private static bool TryParse(
        DocumentUri uri,
        string prefix,
        string suffix,
        out string projectFilePath,
        out string value)
    {
        projectFilePath = string.Empty;
        value = string.Empty;
        string uriText = uri.ToString();
        if (!uriText.StartsWith(prefix, StringComparison.Ordinal) ||
            (!string.IsNullOrEmpty(suffix) &&
                !uriText.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return false;
        }

        int valueEnd = uriText.Length - suffix.Length;
        ReadOnlySpan<char> components = uriText.AsSpan(prefix.Length, valueEnd - prefix.Length);
        int separator = components.IndexOf('/');
        if (separator <= 0 || separator == components.Length - 1 ||
            separator > MaximumComponentLength ||
            components.Length - separator - 1 > MaximumComponentLength)
        {
            return false;
        }

        try
        {
            string projectUriText = DecodeComponent(components[..separator]);
            string decodedValue = DecodeComponent(components[(separator + 1)..]);
            if (string.IsNullOrWhiteSpace(decodedValue))
            {
                return false;
            }

            projectFilePath = DocumentUri.Parse(projectUriText).GetFileSystemPath();
            value = decodedValue;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidOperationException)
        {
            projectFilePath = string.Empty;
            value = string.Empty;
            return false;
        }
    }

    private static string EncodeComponent(string value) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static string DecodeComponent(ReadOnlySpan<char> value) =>
        s_utf8.GetString(Base64Url.DecodeFromChars(value));
}
