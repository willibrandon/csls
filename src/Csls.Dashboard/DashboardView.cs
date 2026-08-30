using Csls.Control.Contracts;
using Hex1b;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Widgets;
using System.Globalization;

namespace Csls.Dashboard;

/// <summary>
/// Builds the declarative Hex1b widget tree for one dashboard state snapshot.
/// </summary>
internal static class DashboardView
{
    private static readonly string[] s_sectionNames = Enum.GetNames<DashboardSection>();

    /// <summary>
    /// Builds the full-screen dashboard widget tree for the current state.
    /// </summary>
    /// <param name="context">The Hex1b widget context.</param>
    /// <param name="state">The selected session and immutable control data.</param>
    /// <returns>The dashboard root widget.</returns>
    internal static Hex1bWidget Build(RootContext context, DashboardState state)
    {
        ControlDashboardSnapshot snapshot = state.Snapshot;
        ListWidget<string> navigation = context
            .List(s_sectionNames)
            .FocusedIndex((int)state.Section)
            .OnSelectionChanged(eventArgs => state.SelectSectionAsync(
                (DashboardSection)eventArgs.SelectedIndex))
            .Fill();
        Hex1bWidget details = BuildDetails(context, state);
        return context.ZStack(stack =>
        [
            stack.WindowPanel()
                .Background(background => background.VStack(vertical =>
                [
                    vertical.Text(string.Create(
                        CultureInfo.InvariantCulture,
                        $"csls dashboard  session {snapshot.Session.ProcessId}  " +
                        $"{snapshot.Session.LifecycleState}  generation {snapshot.Session.WorkspaceGeneration}")),
                    vertical.Text(state.OperationStatus),
                    vertical.HSplitter(
                        left =>
                        [
                            left.Border(nested => [navigation]).Title("Views").Fill()
                        ],
                        right =>
                        [
                            right.Border(nested => [details])
                                .Title(GetSectionTitle(state.Section))
                                .Fill()
                        ],
                        leftWidth: 20).Fill(),
                    vertical.InfoBar(string.Create(
                        CultureInfo.InvariantCulture,
                        $"F2 Requests  F3 Traces  F5 Refresh  F6-F9 Workspace  " +
                        $"F10 Cancel  F11 Trace  Ctrl+C Exit  " +
                        $"updated {state.RefreshedAt:HH:mm:ss} UTC"))
                ]).InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.F2).Action(
                        _ => state.SelectSectionAsync(DashboardSection.Requests),
                        "Show active requests");
                    bindings.Key(Hex1bKey.F3).Action(
                        _ => state.SelectSectionAsync(DashboardSection.Traces),
                        "Show request traces");
                    bindings.Key(Hex1bKey.F5).Action(
                        _ => state.RefreshAsync(),
                        "Refresh live session state");
                    bindings.Key(Hex1bKey.F6).Action(
                        eventArgs => OpenConfirmation(
                            eventArgs.Windows,
                            DashboardOperation.Restore,
                            state),
                        "Restore workspace");
                    bindings.Key(Hex1bKey.F7).Action(
                        eventArgs => OpenConfirmation(
                            eventArgs.Windows,
                            DashboardOperation.Reload,
                            state),
                        "Reload workspace");
                    bindings.Key(Hex1bKey.F8).Action(
                        eventArgs => OpenConfirmation(
                            eventArgs.Windows,
                            DashboardOperation.RestartBuildHosts,
                            state),
                        "Restart build hosts");
                    bindings.Key(Hex1bKey.F9).Action(
                        eventArgs => OpenConfirmation(
                            eventArgs.Windows,
                            DashboardOperation.ClearCaches,
                            state),
                        "Clear caches");
                    bindings.Key(Hex1bKey.F10).Action(
                        eventArgs => OpenRequestCancellation(eventArgs.Windows, state),
                        "Cancel oldest active request");
                    bindings.Key(Hex1bKey.F11).Action(
                        eventArgs => OpenTraceConfirmation(eventArgs.Windows, state),
                        "Start or stop request tracing");
                }))
                .Fill()
        ]);
    }

    private static Hex1bWidget BuildDetails(RootContext context, DashboardState state) =>
        state.Section switch
        {
            DashboardSection.Sessions => BuildSessions(context, state),
            DashboardSection.Actions => BuildActions(context, state),
            DashboardSection.Workspaces => BuildWorkspaces(context, state.Snapshot),
            DashboardSection.Projects => BuildProjects(context, state.Snapshot),
            DashboardSection.Documents => BuildDocuments(context, state.Snapshot),
            DashboardSection.Diagnostics => BuildDiagnostics(context, state),
            DashboardSection.Requests => BuildRequests(context, state.Snapshot),
            DashboardSection.Queues => BuildQueues(context, state.Snapshot),
            DashboardSection.BuildHosts => BuildBuildHosts(context, state.Snapshot),
            DashboardSection.Caches => BuildCaches(context, state.Snapshot),
            DashboardSection.Logs => BuildLogs(context, state.Snapshot),
            DashboardSection.Traces => BuildTraces(context, state.Snapshot),
            _ => throw new InvalidOperationException($"Unknown dashboard section: {state.Section}.")
        };

    private static VStackWidget BuildActions(RootContext context, DashboardState state) =>
        context.VStack(vertical =>
        [
            vertical.Text(state.OperationStatus),
            vertical.Text(""),
            vertical.Button("Restore workspace").OnClick(eventArgs =>
                OpenConfirmation(eventArgs.Windows, DashboardOperation.Restore, state)),
            vertical.Button("Reload workspace").OnClick(eventArgs =>
                OpenConfirmation(eventArgs.Windows, DashboardOperation.Reload, state)),
            vertical.Button("Restart build hosts").OnClick(eventArgs =>
                OpenConfirmation(eventArgs.Windows, DashboardOperation.RestartBuildHosts, state)),
            vertical.Button("Clear caches").OnClick(eventArgs =>
                OpenConfirmation(eventArgs.Windows, DashboardOperation.ClearCaches, state)),
            vertical.Button("Cancel oldest active request").OnClick(eventArgs =>
                OpenRequestCancellation(eventArgs.Windows, state)),
            vertical.Button(state.Snapshot.Requests.Trace.IsActive
                ? "Stop request trace"
                : "Start request trace").OnClick(eventArgs =>
                    OpenTraceConfirmation(eventArgs.Windows, state))
        ]);

    private static void OpenConfirmation(
        WindowManager windows,
        DashboardOperation operation,
        DashboardState state)
    {
        string operationName = GetOperationName(operation);
        windows.Window(window => window.VStack(vertical =>
        [
            vertical.Text(""),
            vertical.Text($"  Run {operationName} for the selected session?"),
            vertical.Text(""),
            vertical.HStack(horizontal =>
            [
                horizontal.Text("  "),
                horizontal.Button("Confirm").OnClick(async _ =>
                {
                    window.Window.CloseWithResult(true);
                    await state.ExecuteOperationAsync(operation).ConfigureAwait(false);
                }),
                horizontal.Text(" "),
                horizontal.Button("Cancel").OnClick(_ => window.Window.CloseWithResult(false))
            ])
        ]))
        .Title("Confirm workspace operation")
        .Size(62, 8)
        .Modal()
        .Open(windows);
    }

    private static void OpenRequestCancellation(
        WindowManager windows,
        DashboardState state)
    {
        IReadOnlyList<ControlRequestInfo> activeRequests =
            state.Snapshot.Requests.ActiveRequests;
        if (activeRequests.Count == 0)
        {
            OpenMessage(windows, "Request cancellation", "No active request is available.");
            return;
        }

        ControlRequestInfo request = activeRequests[0];
        windows.Window(window => window.VStack(vertical =>
        [
            vertical.Text(""),
            vertical.Text($"  Cancel {request.Name}?"),
            vertical.Text($"  {request.CorrelationId:D}"),
            vertical.Text(""),
            vertical.HStack(horizontal =>
            [
                horizontal.Text("  "),
                horizontal.Button("Confirm").OnClick(async _ =>
                {
                    window.Window.CloseWithResult(true);
                    await state.CancelRequestAsync(request.CorrelationId).ConfigureAwait(false);
                }),
                horizontal.Text(" "),
                horizontal.Button("Cancel").OnClick(_ => window.Window.CloseWithResult(false))
            ])
        ]))
        .Title("Confirm request cancellation")
        .Size(72, 9)
        .Modal()
        .Open(windows);
    }

    private static void OpenTraceConfirmation(
        WindowManager windows,
        DashboardState state)
    {
        bool start = !state.Snapshot.Requests.Trace.IsActive;
        windows.Window(window => window.VStack(vertical =>
        [
            vertical.Text(""),
            vertical.Text(start ? "  Start request tracing?" : "  Stop request tracing?"),
            vertical.Text(""),
            vertical.HStack(horizontal =>
            [
                horizontal.Text("  "),
                horizontal.Button("Confirm").OnClick(async _ =>
                {
                    window.Window.CloseWithResult(true);
                    await state.SetTraceAsync(start).ConfigureAwait(false);
                }),
                horizontal.Text(" "),
                horizontal.Button("Cancel").OnClick(_ => window.Window.CloseWithResult(false))
            ])
        ]))
        .Title("Confirm trace operation")
        .Size(62, 8)
        .Modal()
        .Open(windows);
    }

    private static void OpenMessage(
        WindowManager windows,
        string title,
        string message)
    {
        windows.Window(window => window.VStack(vertical =>
        [
            vertical.Text(""),
            vertical.Text($"  {message}"),
            vertical.Text(""),
            vertical.HStack(horizontal =>
            [
                horizontal.Text("  "),
                horizontal.Button("Close").OnClick(_ => window.Window.CloseWithResult(true))
            ])
        ]))
        .Title(title)
        .Size(62, 8)
        .Modal()
        .Open(windows);
    }

    private static TableWidget<ControlSessionInfo> BuildSessions(
        RootContext context,
        DashboardState state) =>
        context.Table(state.Sessions)
            .RowKey(static session => session.ProcessId)
            .Header(header =>
            [
                header.Cell("PID").Width(SizeHint.Fixed(10)),
                header.Cell("State").Width(SizeHint.Fixed(20)),
                header.Cell("Generation").Width(SizeHint.Fixed(12)),
                header.Cell("Workspace").Width(SizeHint.Fill)
            ])
            .Row((row, session, _) =>
            [
                row.Cell(session.ProcessId.ToString(CultureInfo.InvariantCulture)),
                row.Cell(session.LifecycleState),
                row.Cell(session.WorkspaceGeneration.ToString(CultureInfo.InvariantCulture)),
                row.Cell(session.WorkspaceRoots.Count == 0
                    ? "none"
                    : session.WorkspaceRoots[0])
            ])
            .Focus(state.Snapshot.Session.ProcessId)
            .OnFocusChanged(key =>
                key is int processId &&
                processId != state.Snapshot.Session.ProcessId
                    ? state.SelectSessionAsync(processId)
                    : Task.CompletedTask)
            .OnRowActivated((_, session) => state.SelectSessionAsync(session.ProcessId))
            .Fill();

    private static TableWidget<ControlWorkspaceInfo> BuildWorkspaces(
        RootContext context,
        ControlDashboardSnapshot snapshot) =>
        context.Table(snapshot.Workspaces)
            .RowKey(static workspace => workspace.RootPath)
            .Header(header =>
            [
                header.Cell("Root").Width(SizeHint.Fill),
                header.Cell("Kind").Width(SizeHint.Fixed(20)),
                header.Cell("Projects").Width(SizeHint.Fixed(10)),
                header.Cell("Documents").Width(SizeHint.Fixed(11))
            ])
            .Row((row, workspace, _) =>
            [
                row.Cell(workspace.RootPath),
                row.Cell(workspace.WorkspaceKind),
                row.Cell(workspace.ProjectCount.ToString(CultureInfo.InvariantCulture)),
                row.Cell(workspace.DocumentCount.ToString(CultureInfo.InvariantCulture))
            ])
            .Fill();

    private static TableWidget<ControlProjectInfo> BuildProjects(
        RootContext context,
        ControlDashboardSnapshot snapshot) =>
        context.Table(snapshot.Projects)
            .RowKey(static project => project.Id)
            .Header(header =>
            [
                header.Cell("Project").Width(SizeHint.Fill),
                header.Cell("Language").Width(SizeHint.Fixed(12)),
                header.Cell("Documents").Width(SizeHint.Fixed(11)),
                header.Cell("Analyzers").Width(SizeHint.Fixed(10))
            ])
            .Row((row, project, _) =>
            [
                row.Cell(project.Name),
                row.Cell(project.Language),
                row.Cell(project.DocumentCount.ToString(CultureInfo.InvariantCulture)),
                row.Cell(project.AnalyzerReferenceCount.ToString(CultureInfo.InvariantCulture))
            ])
            .Fill();

    private static TableWidget<ControlDocumentInfo> BuildDocuments(
        RootContext context,
        ControlDashboardSnapshot snapshot) =>
        context.Table(snapshot.Documents)
            .RowKey(static document => document.Id)
            .Header(header =>
            [
                header.Cell("Document").Width(SizeHint.Fill),
                header.Cell("Project").Width(SizeHint.Fixed(30)),
                header.Cell("Open").Width(SizeHint.Fixed(6))
            ])
            .Row((row, document, _) =>
            [
                row.Cell(document.Name),
                row.Cell(document.ProjectName),
                row.Cell(document.IsOpen ? "yes" : "no")
            ])
            .Fill();

    private static VStackWidget BuildDiagnostics(
        RootContext context,
        DashboardState state)
    {
        ControlDashboardSnapshot snapshot = state.Snapshot;
        ControlDiagnosticInfo? focusedDiagnostic = state.FocusedDiagnostic;
        TableWidget<ControlDiagnosticInfo> table = context
            .Table(snapshot.Diagnostics)
            .RowKey(DashboardState.GetDiagnosticKey)
            .Header(header =>
            [
                header.Cell("Severity").Width(SizeHint.Fixed(10)),
                header.Cell("Code").Width(SizeHint.Fixed(10)),
                header.Cell("Location").Width(SizeHint.Fill)
            ])
            .Row((row, diagnostic, _) =>
            [
                row.Cell(diagnostic.Severity),
                row.Cell(diagnostic.Id),
                row.Cell(FormatDiagnosticLocation(snapshot, diagnostic))
            ])
            .Focus(state.FocusedDiagnosticKey ?? string.Empty)
            .OnFocusChanged(state.SelectDiagnostic)
            .Fill();
        return context.VStack(vertical =>
        [
            vertical.Text(!snapshot.DiagnosticsLoaded
                ? "Diagnostics load when this view is selected"
                : snapshot.DiagnosticsTruncated
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"Showing {snapshot.Diagnostics.Count} of {snapshot.TotalDiagnostics} diagnostics")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{snapshot.TotalDiagnostics} diagnostics")),
            table,
            vertical.Border(nested =>
            [
                focusedDiagnostic is null
                    ? nested.Text("No diagnostic is selected.")
                    : nested.VStack(details =>
                    [
                        details.Text($"Project: {focusedDiagnostic.ProjectName}"),
                        details.Text(
                            $"Path: {FormatDiagnosticDirectory(snapshot, focusedDiagnostic)}")
                            .Wrap(),
                        details.Text(
                            $"File: {FormatDiagnosticFileLocation(snapshot, focusedDiagnostic)}")
                            .Wrap(),
                        details.Text("Message:"),
                        details.Text(focusedDiagnostic.Message).Wrap()
                    ])
            ])
            .Title("Selected diagnostic")
            .FixedHeight(10)
        ]).Fill();
    }

    private static string FormatDiagnosticLocation(
        ControlDashboardSnapshot snapshot,
        ControlDiagnosticInfo diagnostic)
    {
        string displayPath = GetDiagnosticDisplayPath(snapshot, diagnostic);
        if (displayPath == "-")
        {
            return "-";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{displayPath}:{diagnostic.Line + 1}:{diagnostic.Character + 1}");
    }

    private static string FormatDiagnosticDirectory(
        ControlDashboardSnapshot snapshot,
        ControlDiagnosticInfo diagnostic)
    {
        string displayPath = GetDiagnosticDisplayPath(snapshot, diagnostic);
        return displayPath == "-"
            ? "-"
            : Path.GetDirectoryName(displayPath) ?? ".";
    }

    private static string FormatDiagnosticFileLocation(
        ControlDashboardSnapshot snapshot,
        ControlDiagnosticInfo diagnostic)
    {
        string displayPath = GetDiagnosticDisplayPath(snapshot, diagnostic);
        return displayPath == "-"
            ? "-"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{Path.GetFileName(displayPath)}:{diagnostic.Line + 1}:" +
                $"{diagnostic.Character + 1}");
    }

    private static string GetDiagnosticDisplayPath(
        ControlDashboardSnapshot snapshot,
        ControlDiagnosticInfo diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.FilePath))
        {
            return "-";
        }

        string displayPath = diagnostic.FilePath;
        foreach (ControlWorkspaceInfo workspace in snapshot.Workspaces.OrderByDescending(
            static workspace => workspace.RootPath.Length))
        {
            string relativePath = Path.GetRelativePath(workspace.RootPath, diagnostic.FilePath);
            if (!Path.IsPathRooted(relativePath) &&
                !relativePath.Equals("..", StringComparison.Ordinal) &&
                !relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !relativePath.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                displayPath = relativePath;
                break;
            }
        }

        return displayPath;
    }

    private static VStackWidget BuildRequests(
        RootContext context,
        ControlDashboardSnapshot snapshot)
    {
        ControlRequestSchedulerInfo requests = snapshot.Requests;
        return context.VStack(vertical =>
        [
            vertical.Text($"Accepted: {requests.AcceptedRequests}"),
            vertical.Text($"Completed: {requests.CompletedRequests}"),
            vertical.Text($"Active foreground: {requests.ActiveForegroundRequests}"),
            vertical.Text($"Active background: {requests.ActiveBackgroundRequests}"),
            vertical.Text($"Mutation active: {(requests.IsMutationActive ? "yes" : "no")}"),
            vertical.Text($"Stopping: {(requests.IsStopping ? "yes" : "no")}"),
            vertical.Text(requests.ActiveRequestsTruncated
                ? $"Active requests: showing {requests.ActiveRequests.Count} of " +
                    requests.TotalActiveRequests.ToString(CultureInfo.InvariantCulture)
                : $"Active requests: {requests.TotalActiveRequests}"),
            vertical.Table(requests.ActiveRequests)
                .RowKey(static request => request.CorrelationId)
                .Header(header =>
                [
                    header.Cell("Request").Width(SizeHint.Fill),
                    header.Cell("Mode").Width(SizeHint.Fixed(14)),
                    header.Cell("State").Width(SizeHint.Fixed(12)),
                    header.Cell("Correlation").Width(SizeHint.Fixed(36))
                ])
                .Row((row, request, _) =>
                [
                    row.Cell(request.Name),
                    row.Cell(request.Mode),
                    row.Cell(request.Status),
                    row.Cell(request.CorrelationId.ToString("D"))
                ])
                .Fill()
        ]).Fill();
    }

    private static VStackWidget BuildTraces(
        RootContext context,
        ControlDashboardSnapshot snapshot)
    {
        ControlTraceInfo trace = snapshot.Requests.Trace;
        return context.VStack(vertical =>
        [
            vertical.Text(trace.IsActive ? "Trace: active" : "Trace: stopped"),
            vertical.Text($"Trace ID: {trace.TraceId?.ToString("D") ?? "none"}"),
            vertical.Text($"Entries: {trace.Entries.Count} / {trace.Capacity}"),
            vertical.Text($"Dropped: {trace.DroppedEntries}"),
            vertical.Table(trace.Entries)
                .RowKey(static entry => entry.Ordinal)
                .Header(header =>
                [
                    header.Cell("Request").Width(SizeHint.Fill),
                    header.Cell("State").Width(SizeHint.Fixed(12)),
                    header.Cell("Duration ms").Width(SizeHint.Fixed(14)),
                    header.Cell("Correlation").Width(SizeHint.Fixed(36))
                ])
                .Row((row, entry, _) =>
                [
                    row.Cell(entry.Name),
                    row.Cell(entry.Status),
                    row.Cell(entry.DurationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)),
                    row.Cell(entry.CorrelationId.ToString("D"))
                ])
                .Fill()
        ]).Fill();
    }

    private static VStackWidget BuildQueues(
        RootContext context,
        ControlDashboardSnapshot snapshot)
    {
        ControlRequestSchedulerInfo requests = snapshot.Requests;
        return context.VStack(vertical =>
        [
            vertical.Text($"Queued: {requests.QueuedRequests} / {requests.Capacity}"),
            vertical.Text($"Foreground concurrency: {requests.ForegroundConcurrency}"),
            vertical.Text($"Background concurrency: {requests.BackgroundConcurrency}")
        ]);
    }

    private static TableWidget<ControlBuildHostInfo> BuildBuildHosts(
        RootContext context,
        ControlDashboardSnapshot snapshot) =>
        context.Table(snapshot.BuildHosts)
            .RowKey(static host => string.Concat(
                host.ProcessId.ToString(CultureInfo.InvariantCulture),
                "|",
                host.WorkspaceRoot))
            .Header(header =>
            [
                header.Cell("PID").Width(SizeHint.Fixed(10)),
                header.Cell("Kind").Width(SizeHint.Fixed(24)),
                header.Cell("Projects").Width(SizeHint.Fixed(10)),
                header.Cell("Workspace").Width(SizeHint.Fill)
            ])
            .Row((row, host, _) =>
            [
                row.Cell(host.ProcessId.ToString(CultureInfo.InvariantCulture)),
                row.Cell(host.Kind),
                row.Cell(host.ProjectCount.ToString(CultureInfo.InvariantCulture)),
                row.Cell(host.WorkspaceRoot)
            ])
            .Fill();

    private static TableWidget<ControlCacheInfo> BuildCaches(
        RootContext context,
        ControlDashboardSnapshot snapshot) =>
        context.Table(snapshot.Caches)
            .RowKey(static cache => cache.Name)
            .Header(header =>
            [
                header.Cell("Cache").Width(SizeHint.Fill),
                header.Cell("Entries").Width(SizeHint.Fixed(12)),
                header.Cell("Capacity").Width(SizeHint.Fixed(12))
            ])
            .Row((row, cache, _) =>
            [
                row.Cell(cache.Name),
                row.Cell(cache.EntryCount.ToString(CultureInfo.InvariantCulture)),
                row.Cell(cache.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "dynamic")
            ])
            .Fill();

    private static TableWidget<ControlLogEntry> BuildLogs(
        RootContext context,
        ControlDashboardSnapshot snapshot) =>
        context.Table(snapshot.Logs)
            .RowKey(static entry => entry.Sequence)
            .Header(header =>
            [
                header.Cell("Time").Width(SizeHint.Fixed(12)),
                header.Cell("Level").Width(SizeHint.Fixed(12)),
                header.Cell("Category").Width(SizeHint.Fixed(30)),
                header.Cell("Message").Width(SizeHint.Fill)
            ])
            .Row((row, entry, _) =>
            [
                row.Cell(entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                row.Cell(entry.Level),
                row.Cell(entry.Category),
                row.Cell(entry.Message)
            ])
            .Fill();

    private static string GetSectionTitle(DashboardSection section) => section switch
    {
        DashboardSection.BuildHosts => "Build hosts",
        _ => section.ToString()
    };

    private static string GetOperationName(DashboardOperation operation) => operation switch
    {
        DashboardOperation.Restore => "restore workspace",
        DashboardOperation.Reload => "reload workspace",
        DashboardOperation.RestartBuildHosts => "restart build hosts",
        DashboardOperation.ClearCaches => "clear caches",
        _ => throw new InvalidOperationException($"Unknown dashboard operation: {operation}.")
    };
}
