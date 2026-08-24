using Csls.Core;
using Csls.Protocol;
using Csls.Workspaces;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    private static readonly IReadOnlyList<FileOperationFilter> s_fileOperationFilters =
        Array.AsReadOnly(
        [
            CreateFileOperationFilter(
                "**/*.{cs,csx,cshtml,razor,csproj,sln,slnx,props,targets,ruleset,globalconfig}",
                FileOperationPatternKind.File),
            CreateFileOperationFilter(
                "**/{global.json,packages.config,NuGet.config,.editorconfig}",
                FileOperationPatternKind.File),
            CreateFileOperationFilter("**", FileOperationPatternKind.Folder)
        ]);

    /// <inheritdoc />
    public Task DidCreateFilesAsync(
        CreateFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        return ApplyWorkspaceFileOperationsAsync(
            "workspace/didCreateFiles",
            token => _workspaceManager.ApplyCreatedFilesAsync(parameters, token),
            clearUris: [],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DidRenameFilesAsync(
        RenameFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        return ApplyWorkspaceFileOperationsAsync(
            "workspace/didRenameFiles",
            token => _workspaceManager.ApplyRenamedFilesAsync(parameters, token),
            [.. parameters.Files.Select(static file => file.OldUri)],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DidDeleteFilesAsync(
        DeleteFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        return ApplyWorkspaceFileOperationsAsync(
            "workspace/didDeleteFiles",
            token => _workspaceManager.ApplyDeletedFilesAsync(parameters, token),
            [.. parameters.Files.Select(static file => file.Uri)],
            cancellationToken);
    }

    private static FileOperationOptions CreateFileOperationOptions()
    {
        var registration = new FileOperationRegistrationOptions
        {
            Filters = s_fileOperationFilters
        };
        return new FileOperationOptions
        {
            DidCreate = registration,
            DidRename = registration,
            DidDelete = registration
        };
    }

    private static FileOperationFilter CreateFileOperationFilter(
        string glob,
        string matches) =>
        new()
        {
            Scheme = Uri.UriSchemeFile,
            Pattern = new FileOperationPattern
            {
                Glob = glob,
                Matches = matches,
                Options = new FileOperationPatternOptions
                {
                    IgnoreCase = OperatingSystem.IsWindows()
                }
            }
        };

    private async Task ApplyWorkspaceFileOperationsAsync(
        string requestName,
        Func<CancellationToken, Task<WorkspaceMaintenanceResult?>> applyAsync,
        IReadOnlyList<DocumentUri> clearUris,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        (long RequestId, IReadOnlyList<DocumentUri> PublishUris) result =
            await _scheduler.ScheduleAsync(
                requestName,
                RequestMode.ReadWrite,
                () => _workspaceManager.Generation,
                async context =>
                {
                    WorkspaceMaintenanceResult? refresh = await applyAsync(
                        context.CancellationToken).ConfigureAwait(false);
                    if (refresh is null)
                    {
                        return (context.Ordinal, Array.Empty<DocumentUri>());
                    }

                    ClearPushDiagnosticRequests();
                    DocumentUri[] publishUris =
                    [
                        .. _workspaceManager
                            .GetOpenDocumentUris()
                            .Concat(clearUris)
                            .Distinct()
                    ];
                    foreach (DocumentUri uri in publishUris)
                    {
                        RegisterPushDiagnosticRequest(uri, context.Ordinal);
                    }

                    return (context.Ordinal, publishUris);
                },
                cancellationToken).ConfigureAwait(false);

        foreach (DocumentUri uri in result.PublishUris)
        {
            await PublishDiagnosticsAsync(
                uri,
                result.RequestId,
                delay: false,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
