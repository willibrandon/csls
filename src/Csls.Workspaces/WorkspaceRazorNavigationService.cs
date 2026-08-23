using Csls.Protocol;
using Microsoft.CodeAnalysis;
using LspLocation = Csls.Protocol.Location;

namespace Csls.Workspaces;

/// <summary>
/// Resolves semantic navigation from Razor source through SDK-generated C# documents.
/// </summary>
internal static class WorkspaceRazorNavigationService
{
    /// <summary>
    /// Finds source definitions for a symbol at one Razor source position.
    /// </summary>
    /// <param name="solution">The immutable workspace solution snapshot.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source definition locations.</returns>
    internal static Task<IReadOnlyList<LspLocation>> GetDefinitionsAsync(
        Solution solution,
        string path,
        Position position,
        CancellationToken cancellationToken) =>
        GetAsync(
            solution,
            path,
            position,
            WorkspaceNavigationService.GetDefinitionsAsync,
            cancellationToken);

    /// <summary>
    /// Finds source declarations for a symbol at one Razor source position.
    /// </summary>
    /// <param name="solution">The immutable workspace solution snapshot.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source declaration locations.</returns>
    internal static Task<IReadOnlyList<LspLocation>> GetDeclarationsAsync(
        Solution solution,
        string path,
        Position position,
        CancellationToken cancellationToken) =>
        GetAsync(
            solution,
            path,
            position,
            WorkspaceNavigationService.GetDeclarationsAsync,
            cancellationToken);

    /// <summary>
    /// Finds source type definitions for a symbol at one Razor source position.
    /// </summary>
    /// <param name="solution">The immutable workspace solution snapshot.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source type-definition locations.</returns>
    internal static Task<IReadOnlyList<LspLocation>> GetTypeDefinitionsAsync(
        Solution solution,
        string path,
        Position position,
        CancellationToken cancellationToken) =>
        GetAsync(
            solution,
            path,
            position,
            WorkspaceNavigationService.GetTypeDefinitionsAsync,
            cancellationToken);

    /// <summary>
    /// Finds source implementations for a symbol at one Razor source position.
    /// </summary>
    /// <param name="solution">The immutable workspace solution snapshot.</param>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The bounded source implementation locations.</returns>
    internal static Task<IReadOnlyList<LspLocation>> GetImplementationsAsync(
        Solution solution,
        string path,
        Position position,
        CancellationToken cancellationToken) =>
        GetAsync(
            solution,
            path,
            position,
            WorkspaceNavigationService.GetImplementationsAsync,
            cancellationToken);

    private static async Task<IReadOnlyList<LspLocation>> GetAsync(
        Solution solution,
        string path,
        Position position,
        Func<Document, int, CancellationToken, Task<IReadOnlyList<LspLocation>>> navigateAsync,
        CancellationToken cancellationToken)
    {
        RazorMappedDocument? mappedDocument = await WorkspaceRazorMappingService.ResolveAsync(
            solution,
            path,
            position,
            cancellationToken).ConfigureAwait(false);
        return mappedDocument is null
            ? []
            : await navigateAsync(
                mappedDocument.Document,
                mappedDocument.GeneratedOffset,
                cancellationToken).ConfigureAwait(false);
    }
}
