using Csls.Core;
using Csls.Protocol;
using Csls.Workspaces;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using LspFileSystemWatcher = Csls.Protocol.FileSystemWatcher;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    /// <inheritdoc />
    public async Task DidCreateFilesAsync(
        CreateFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        _ = await ApplyWorkspaceFileOperationsAsync(
            "workspace/didCreateFiles",
            token => _workspaceManager.ApplyCreatedFilesAsync(parameters, token),
            clearUris: [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DidRenameFilesAsync(
        RenameFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        _ = await ApplyWorkspaceFileOperationsAsync(
            "workspace/didRenameFiles",
            token => _workspaceManager.ApplyRenamedFilesAsync(parameters, token),
            [.. parameters.Files.Select(static file => file.OldUri)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DidDeleteFilesAsync(
        DeleteFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Files);
        _ = await ApplyWorkspaceFileOperationsAsync(
            "workspace/didDeleteFiles",
            token => _workspaceManager.ApplyDeletedFilesAsync(parameters, token),
            [.. parameters.Files.Select(static file => file.Uri)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DidChangeWatchedFilesAsync(
        DidChangeWatchedFilesParams parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.Changes);
        long startedTimestamp = Stopwatch.GetTimestamp();
        WorkspaceMaintenanceResult? result = await ApplyWorkspaceFileOperationsAsync(
            "workspace/didChangeWatchedFiles",
            token => _workspaceManager.ApplyChangedFilesAsync(parameters, token),
            clearUris: [],
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }

        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
        {
            long elapsedMilliseconds =
                (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            string updateMode = result.RestartedBuildHostCount > 0
                ? "full reload"
                : "incremental update";
            string paths = FormatWatchedFilePaths(parameters.Changes);
            LanguageServerLogger.LogWatchedFileChangesCompleted(
                _logger,
                elapsedMilliseconds,
                updateMode,
                paths);
        }
    }

    private static FileOperationOptions CreateFileOperationOptions()
    {
        var registration = new FileOperationRegistrationOptions
        {
            Filters = CreateFileOperationFilters()
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

    private static ReadOnlyCollection<FileOperationFilter> CreateFileOperationFilters() =>
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

    private static ReadOnlyCollection<LspFileSystemWatcher> CreateFileSystemWatchers() =>
        Array.AsReadOnly(
        [
            new LspFileSystemWatcher
            {
                GlobPattern =
                    "**/*.{cs,csx,cshtml,razor,csproj,sln,slnx,props,targets,ruleset,globalconfig}",
                Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
            },
            new LspFileSystemWatcher
            {
                GlobPattern = "**/{global.json,packages.config,NuGet.config,.editorconfig}",
                Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
            }
        ]);

    private async Task RegisterFileWatchersAsync(CancellationToken cancellationToken)
    {
        var options = new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = CreateFileSystemWatchers()
        };
        JsonElement registerOptions = JsonSerializer.SerializeToElement(
            options,
            LspJsonSerializerContext.Default.DidChangeWatchedFilesRegistrationOptions);
        await _client.RegisterCapabilityAsync(
            new RegistrationParams
            {
                Registrations =
                [
                    new Registration
                    {
                        Id = "csls-workspace-watchers",
                        Method = "workspace/didChangeWatchedFiles",
                        RegisterOptions = registerOptions
                    }
                ]
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkspaceMaintenanceResult?> ApplyWorkspaceFileOperationsAsync(
        string requestName,
        Func<CancellationToken, Task<WorkspaceMaintenanceResult?>> applyAsync,
        IReadOnlyList<DocumentUri> clearUris,
        CancellationToken cancellationToken)
    {
        EnsureRunning();
        (
            long RequestId,
            IReadOnlyList<DocumentUri> PublishUris,
            bool WorkspaceChanged,
            WorkspaceMaintenanceResult? Maintenance
        ) =
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
                        return (
                            context.Ordinal,
                            Array.Empty<DocumentUri>(),
                            false,
                            refresh);
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

                    return (context.Ordinal, publishUris, true, refresh);
                },
                cancellationToken).ConfigureAwait(false);

        foreach (DocumentUri uri in PublishUris)
        {
            await PublishDiagnosticsAsync(
                uri,
                RequestId,
                delay: false,
                cancellationToken).ConfigureAwait(false);
        }

        if (_supportsPullDiagnostics &&
            _supportsDiagnosticRefresh &&
            WorkspaceChanged)
        {
            await _client.RefreshDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        }

        return Maintenance;
    }

    private static string FormatWatchedFilePaths(IReadOnlyList<FileEvent> changes)
    {
        const int maximumDisplayedPaths = 8;
        string[] distinctPaths =
        [
            .. changes
                .Select(static change => change.Uri.GetFileSystemPath())
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
        ];
        string paths = string.Join(
            ", ",
            distinctPaths.Take(maximumDisplayedPaths));
        return distinctPaths.Length <= maximumDisplayedPaths
            ? paths
            : $"{paths}, ... (+{distinctPaths.Length - maximumDisplayedPaths} more)";
    }
}
