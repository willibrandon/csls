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
                var roots = new List<string>(_workspaceManager.WorkspaceRoots.Count);
                foreach (string root in _workspaceManager.WorkspaceRoots)
                {
                    if (!removed.Contains(root))
                    {
                        roots.Add(root);
                    }
                }

                foreach (WorkspaceFolder folder in parameters.Event.Added)
                {
                    string root = Path.GetFullPath(folder.Uri.GetFileSystemPath());
                    if (!roots.Contains(root, comparer))
                    {
                        roots.Add(root);
                    }
                }

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
