using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Supplies the built real-process target to the VS Code pipe-transport acceptance test.
/// </summary>
internal static class VsCodePipeTransportFixture
{
    /// <summary>
    /// Writes the exact managed target path into the temporary editor workspace.
    /// </summary>
    /// <param name="workspacePath">The owned editor workspace.</param>
    /// <param name="cancellationToken">Cancels fixture publication.</param>
    /// <returns>A task that completes when the target manifest is durable.</returns>
    internal static Task PrepareAsync(string workspacePath, CancellationToken cancellationToken)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string target = EditorToolResolver.ResolveTestProcessHost(repositoryRoot);
        string manifestPath = Path.Join(workspacePath, ".vscode", "pipe-transport-fixture.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        return File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(new { program = target }), cancellationToken);
    }
}
