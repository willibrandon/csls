using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol;

namespace Csls.Mcp.Worker;

/// <summary>
/// Validates document inputs shared by MCP inspection and editing tools.
/// </summary>
internal static class McpDocumentValidation
{
    private const int MaximumPathLength = 4096;

    /// <summary>
    /// Creates a navigation request from validated document coordinates.
    /// </summary>
    /// <param name="documentPath">The path of the requested document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    /// <param name="includeDeclaration">Whether references include the declaration.</param>
    /// <returns>The normalized navigation request.</returns>
    internal static ControlNavigationRequest CreateNavigationRequest(
        string documentPath,
        int line,
        int character,
        bool includeDeclaration)
    {
        ValidateDocumentPosition(documentPath, line, character);
        return new ControlNavigationRequest
        {
            DocumentPath = Path.GetFullPath(documentPath),
            Position = new Position(line, character),
            IncludeDeclaration = includeDeclaration
        };
    }

    /// <summary>
    /// Validates the document path's presence and length.
    /// </summary>
    /// <param name="documentPath">The path of the requested document.</param>
    internal static void ValidateDocumentPath(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath) ||
            documentPath.Length > MaximumPathLength)
        {
            throw new McpException(
                $"documentPath must contain between 1 and {MaximumPathLength} characters.");
        }
    }

    /// <summary>
    /// Validates a document path and zero-based source position.
    /// </summary>
    /// <param name="documentPath">The path of the requested document.</param>
    /// <param name="line">The zero-based document line.</param>
    /// <param name="character">The zero-based UTF-16 character offset.</param>
    internal static void ValidateDocumentPosition(
        string documentPath,
        int line,
        int character)
    {
        ValidateDocumentPath(documentPath);
        if (line < 0)
        {
            throw new McpException("line must be zero or greater.");
        }

        if (character < 0)
        {
            throw new McpException("character must be zero or greater.");
        }
    }
}
