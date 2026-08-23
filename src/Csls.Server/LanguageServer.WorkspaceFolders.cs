using Csls.Core;
using Csls.Protocol;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    /// <inheritdoc />
    public Task DidChangeWorkspaceFoldersAsync(
        DidChangeWorkspaceFoldersParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        EnsureRunning();
        return _scheduler.ScheduleAsync(
            "workspace/didChangeWorkspaceFolders",
            RequestMode.ReadWrite,
            () => _workspaceManager.Generation,
            async context =>
            {
                StringComparer comparer = OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
                var removed = new HashSet<string>(
                    parameters.Event.Removed.Select(static folder =>
                        Path.GetFullPath(folder.Uri.GetFileSystemPath())),
                    comparer);
                List<string> roots =
                [
                    .. _workspaceManager.WorkspaceRoots.Where(root => !removed.Contains(root))
                ];
                string[] addedRoots =
                [
                    .. parameters.Event.Added
                        .Select(static folder => Path.GetFullPath(folder.Uri.GetFileSystemPath()))
                        .Distinct(comparer)
                        .Where(root => !roots.Contains(root, comparer))
                ];
                roots.AddRange(addedRoots);

                if (_workspaceManager.WorkspaceRoots.SequenceEqual(roots, comparer))
                {
                    return false;
                }

                _semanticTokensCache.Clear();
                await _workspaceManager
                    .ChangeWorkspaceFoldersAsync(roots, context.CancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }
}
