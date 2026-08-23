using Csls.Protocol;

namespace Csls.Server;

/// <summary>
/// Stores one document-scoped encoded semantic-token result for delta computation.
/// </summary>
internal sealed record SemanticTokensCacheEntry
{
    /// <summary>
    /// Gets the document that produced this encoded result.
    /// </summary>
    internal required DocumentUri DocumentUri { get; init; }

    /// <summary>
    /// Gets the immutable encoded integer sequence returned to the client.
    /// </summary>
    internal required IReadOnlyList<int> Data { get; init; }
}
