using Csls.Control.Contracts;
using Hex1b;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Theming;
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
                    bindings.Key(Hex1bKey.Y).Action(
                        eventArgs => YankFocusedRowAsync(eventArgs, state),
                        "Yank focused row");
                }))
                .Fill()
        ]);
    }

    private static Hex1bWidget BuildDetails(RootContext context, DashboardState state) =>
        state.Section switch
        {
            DashboardSection.Sessions => BuildSessions(context, state),
            DashboardSection.Actions => BuildActions(context, state),
            DashboardSection.Workspaces => BuildWorkspaces(context, state),
            DashboardSection.Projects => BuildProjects(context, state),
            DashboardSection.Documents => BuildDocuments(context, state),
            DashboardSection.Diagnostics => BuildDiagnostics(context, state),
            DashboardSection.Requests => BuildRequests(context, state),
            DashboardSection.Queues => BuildQueues(context, state.Snapshot),
            DashboardSection.BuildHosts => BuildBuildHosts(context, state),
            DashboardSection.Caches => BuildCaches(context, state),
            DashboardSection.Logs => BuildLogs(context, state),
            DashboardSection.Traces => BuildTraces(context, state),
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
                ResizableHeader(header, state, DashboardSection.Sessions, 0, "PID", 10),
                ResizableHeader(header, state, DashboardSection.Sessions, 1, "State", 20),
                ResizableHeader(header, state, DashboardSection.Sessions, 2, "Generation", 12),
                header.Cell("Workspace").Width(SizeHint.Fill)
            ])
            .Row((row, session, _) =>
            {
                bool flash = state.IsYankFlashing(
                    DashboardSection.Sessions,
                    session.ProcessId);
                return
                [
                    RowCell(
                        row,
                        session.ProcessId.ToString(CultureInfo.InvariantCulture),
                        flash),
                    RowCell(row, session.LifecycleState, flash),
                    RowCell(
                        row,
                        session.WorkspaceGeneration.ToString(CultureInfo.InvariantCulture),
                        flash),
                    RowCell(
                        row,
                        session.WorkspaceRoots.Count == 0
                            ? "none"
                            : session.WorkspaceRoots[0],
                        flash)
                ];
            })
            .Focus(state.GetFocusedRow(
                DashboardSection.Sessions,
                state.Snapshot.Session.ProcessId))
            .OnFocusChanged(key =>
            {
                state.SetFocusedRow(DashboardSection.Sessions, key);
                return key is int processId &&
                    processId != state.Snapshot.Session.ProcessId
                        ? state.SelectSessionAsync(processId)
                        : Task.CompletedTask;
            })
            .OnRowActivated((_, session) => state.SelectSessionAsync(session.ProcessId))
            .Fill();

    private static TableWidget<ControlWorkspaceInfo> BuildWorkspaces(
        RootContext context,
        DashboardState state) =>
        context.Table(state.Snapshot.Workspaces)
            .RowKey(static workspace => workspace.RootPath)
            .Header(header =>
            [
                ResizableHeader(header, state, DashboardSection.Workspaces, 0, "Root", 36),
                ResizableHeader(header, state, DashboardSection.Workspaces, 1, "Kind", 20),
                ResizableHeader(header, state, DashboardSection.Workspaces, 2, "Projects", 10),
                header.Cell("Documents").Width(SizeHint.Fill)
            ])
            .Row((row, workspace, _) => BuildRowCells(
                row,
                state,
                DashboardSection.Workspaces,
                workspace.RootPath,
                workspace.RootPath,
                workspace.WorkspaceKind,
                workspace.ProjectCount.ToString(CultureInfo.InvariantCulture),
                workspace.DocumentCount.ToString(CultureInfo.InvariantCulture)))
            .Focus(state.GetFocusedRow(
                DashboardSection.Workspaces,
                state.Snapshot.Workspaces.Count == 0
                    ? null
                    : state.Snapshot.Workspaces[0].RootPath))
            .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.Workspaces, key))
            .Fill();

    private static TableWidget<ControlProjectInfo> BuildProjects(
        RootContext context,
        DashboardState state) =>
        context.Table(state.Snapshot.Projects)
            .RowKey(static project => project.Id)
            .Header(header =>
            [
                ResizableHeader(header, state, DashboardSection.Projects, 0, "Project", 30),
                ResizableHeader(header, state, DashboardSection.Projects, 1, "Language", 12),
                ResizableHeader(header, state, DashboardSection.Projects, 2, "Documents", 11),
                header.Cell("Analyzers").Width(SizeHint.Fill)
            ])
            .Row((row, project, _) => BuildRowCells(
                row,
                state,
                DashboardSection.Projects,
                project.Id,
                project.Name,
                project.Language,
                project.DocumentCount.ToString(CultureInfo.InvariantCulture),
                project.AnalyzerReferenceCount.ToString(CultureInfo.InvariantCulture)))
            .Focus(state.GetFocusedRow(
                DashboardSection.Projects,
                state.Snapshot.Projects.Count == 0
                    ? null
                    : state.Snapshot.Projects[0].Id))
            .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.Projects, key))
            .Fill();

    private static TableWidget<ControlDocumentInfo> BuildDocuments(
        RootContext context,
        DashboardState state) =>
        context.Table(state.Snapshot.Documents)
            .RowKey(static document => document.Id)
            .Header(header =>
            [
                ResizableHeader(header, state, DashboardSection.Documents, 0, "Document", 32),
                ResizableHeader(header, state, DashboardSection.Documents, 1, "Project", 30),
                header.Cell("Open").Width(SizeHint.Fill)
            ])
            .Row((row, document, _) => BuildRowCells(
                row,
                state,
                DashboardSection.Documents,
                document.Id,
                document.Name,
                document.ProjectName,
                document.IsOpen ? "yes" : "no"))
            .Focus(state.GetFocusedRow(
                DashboardSection.Documents,
                state.Snapshot.Documents.Count == 0
                    ? null
                    : state.Snapshot.Documents[0].Id))
            .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.Documents, key))
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
                ResizableHeader(
                    header,
                    state,
                    DashboardSection.Diagnostics,
                    column: 0,
                    "Severity",
                    defaultWidth: 10),
                ResizableHeader(
                    header,
                    state,
                    DashboardSection.Diagnostics,
                    column: 1,
                    "Code",
                    defaultWidth: 10),
                header.Cell("Location").Width(SizeHint.Fill)
            ])
            .Row((row, diagnostic, _) =>
            [
                RowCell(
                    row,
                    diagnostic.Severity,
                    state.IsYankFlashing(
                        DashboardSection.Diagnostics,
                        DashboardState.GetDiagnosticKey(diagnostic))),
                RowCell(
                    row,
                    diagnostic.Id,
                    state.IsYankFlashing(
                        DashboardSection.Diagnostics,
                        DashboardState.GetDiagnosticKey(diagnostic))),
                RowCell(
                    row,
                    FormatDiagnosticLocation(snapshot, diagnostic),
                    state.IsYankFlashing(
                        DashboardSection.Diagnostics,
                        DashboardState.GetDiagnosticKey(diagnostic)))
            ])
            .Focus(state.FocusedDiagnosticKey ?? string.Empty)
            .OnFocusChanged(state.SelectDiagnostic)
            .Fill();
        VStackWidget diagnostics = context.VStack(vertical =>
        [
            vertical.Text(state.DiagnosticsLoading
                ? "Loading diagnostics..."
                : state.DiagnosticsLoadError ?? (!snapshot.DiagnosticsLoaded
                ? "Diagnostics have not loaded"
                : snapshot.DiagnosticsTruncated
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"Showing {snapshot.Diagnostics.Count} of {snapshot.TotalDiagnostics} diagnostics")
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"{snapshot.TotalDiagnostics} diagnostics"))),
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
        return state.DiagnosticsLoading
            ? diagnostics.RedrawAfter(50)
            : diagnostics;
    }

    private static TableCell ResizableHeader(
        TableHeaderContext header,
        DashboardState state,
        DashboardSection section,
        int column,
        string title,
        int defaultWidth)
    {
        int width = state.GetColumnWidth(section, column, defaultWidth);
        return header.Cell(cell => cell.HStack(horizontal =>
        [
            horizontal.Text(title).Fill(),
            horizontal.Interactable(interactable => interactable.Text("↔"))
                .InputBindings(bindings =>
                    bindings.Drag(MouseButton.Left).Action(
                        (_, _) => DragHandler.Simple(
                            onMove: (deltaX, _) => state.ResizeColumn(
                                section,
                                column,
                                width,
                                deltaX)),
                        "Resize column"))
                .FixedWidth(1)
        ])).Width(SizeHint.Fixed(width));
    }

    private static Task YankFocusedRowAsync(
        InputBindingActionContext context,
        DashboardState state)
    {
        (object Key, string Text)? yank = state.Section switch
        {
            DashboardSection.Sessions => GetFocusedRowYank(
                state,
                state.Sessions,
                DashboardSection.Sessions,
                static session => session.ProcessId,
                static session =>
                [
                    session.ProcessId.ToString(CultureInfo.InvariantCulture),
                    session.LifecycleState,
                    session.WorkspaceGeneration.ToString(CultureInfo.InvariantCulture),
                    session.WorkspaceRoots.Count == 0 ? "none" : session.WorkspaceRoots[0]
                ]),
            DashboardSection.Workspaces => GetFocusedRowYank(
                state,
                state.Snapshot.Workspaces,
                DashboardSection.Workspaces,
                static workspace => workspace.RootPath,
                static workspace =>
                [
                    workspace.RootPath,
                    workspace.WorkspaceKind,
                    workspace.ProjectCount.ToString(CultureInfo.InvariantCulture),
                    workspace.DocumentCount.ToString(CultureInfo.InvariantCulture)
                ]),
            DashboardSection.Projects => GetFocusedRowYank(
                state,
                state.Snapshot.Projects,
                DashboardSection.Projects,
                static project => project.Id,
                static project =>
                [
                    project.Name,
                    project.Language,
                    project.DocumentCount.ToString(CultureInfo.InvariantCulture),
                    project.AnalyzerReferenceCount.ToString(CultureInfo.InvariantCulture)
                ]),
            DashboardSection.Documents => GetFocusedRowYank(
                state,
                state.Snapshot.Documents,
                DashboardSection.Documents,
                static document => document.Id,
                static document =>
                [
                    document.Name,
                    document.ProjectName,
                    document.IsOpen ? "yes" : "no"
                ]),
            DashboardSection.Diagnostics => GetFocusedRowYank(
                state,
                state.Snapshot.Diagnostics,
                DashboardSection.Diagnostics,
                DashboardState.GetDiagnosticKey,
                diagnostic =>
                [
                    diagnostic.Severity,
                    diagnostic.Id,
                    FormatDiagnosticLocation(state.Snapshot, diagnostic)
                ]),
            DashboardSection.Requests => GetFocusedRowYank(
                state,
                state.Snapshot.Requests.ActiveRequests,
                DashboardSection.Requests,
                static request => request.CorrelationId,
                static request =>
                [
                    request.Name,
                    request.Mode,
                    request.Status,
                    request.CorrelationId.ToString("D")
                ]),
            DashboardSection.BuildHosts => GetFocusedRowYank(
                state,
                state.Snapshot.BuildHosts,
                DashboardSection.BuildHosts,
                static host => GetBuildHostKey(host),
                static host =>
                [
                    host.ProcessId.ToString(CultureInfo.InvariantCulture),
                    host.Kind,
                    host.WorkspaceCount.ToString(CultureInfo.InvariantCulture),
                    host.ProjectCount.ToString(CultureInfo.InvariantCulture)
                ]),
            DashboardSection.Caches => GetFocusedRowYank(
                state,
                state.Snapshot.Caches,
                DashboardSection.Caches,
                static cache => cache.Name,
                static cache =>
                [
                    cache.Name,
                    cache.EntryCount.ToString(CultureInfo.InvariantCulture),
                    cache.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "dynamic"
                ]),
            DashboardSection.Logs => GetFocusedRowYank(
                state,
                state.Snapshot.Logs,
                DashboardSection.Logs,
                static entry => entry.Sequence,
                static entry =>
                [
                    entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    entry.Level,
                    entry.Category,
                    entry.Message
                ]),
            DashboardSection.Traces => GetFocusedRowYank(
                state,
                state.Snapshot.Requests.Trace.Entries,
                DashboardSection.Traces,
                static entry => entry.Ordinal,
                static entry =>
                [
                    entry.Name,
                    entry.Status,
                    entry.DurationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    entry.CorrelationId.ToString("D")
                ]),
            _ => null
        };
        if (yank is null)
        {
            return Task.CompletedTask;
        }

        context.CopyToClipboard(yank.Value.Text);
        state.FlashYankedRow(state.Section, yank.Value.Key);
        return Task.CompletedTask;
    }

    private static (object Key, string Text)? GetFocusedRowYank<TRow>(
        DashboardState state,
        IReadOnlyList<TRow> rows,
        DashboardSection section,
        Func<TRow, object> keySelector,
        Func<TRow, IReadOnlyList<string>> valueSelector)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        object? focusedKey = state.GetFocusedRow(section, keySelector(rows[0]));
        TRow? focusedRow = rows.FirstOrDefault(row => Equals(keySelector(row), focusedKey));
        return focusedRow is null
            ? null
            : (keySelector(focusedRow), string.Join('\t', valueSelector(focusedRow)));
    }

    private static TableCell RowCell(
        TableRowContext row,
        string text,
        bool yankFlash) => yankFlash
            ? row.Cell(cell => cell.ThemePanel(
                theme => theme
                    .Set(
                        GlobalTheme.ForegroundColor,
                        Hex1bColor.FromRgb(24, 24, 37))
                    .Set(
                        GlobalTheme.BackgroundColor,
                        Hex1bColor.FromRgb(126, 201, 216)),
                cell.Text(text)))
            : row.Cell(text);

    private static IReadOnlyList<TableCell> BuildRowCells(
        TableRowContext row,
        DashboardState state,
        DashboardSection section,
        object key,
        params string[] values)
    {
        bool yankFlash = state.IsYankFlashing(section, key);
        return [.. values.Select(value => RowCell(row, value, yankFlash))];
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

        return OperatingSystem.IsWindows()
            ? displayPath.Replace('\\', '/')
            : displayPath;
    }

    private static VStackWidget BuildRequests(
        RootContext context,
        DashboardState state)
    {
        ControlRequestSchedulerInfo requests = state.Snapshot.Requests;
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
                    ResizableHeader(header, state, DashboardSection.Requests, 0, "Request", 30),
                    ResizableHeader(header, state, DashboardSection.Requests, 1, "Mode", 14),
                    ResizableHeader(header, state, DashboardSection.Requests, 2, "State", 12),
                    header.Cell("Correlation").Width(SizeHint.Fill)
                ])
                .Row((row, request, _) => BuildRowCells(
                    row,
                    state,
                    DashboardSection.Requests,
                    request.CorrelationId,
                    request.Name,
                    request.Mode,
                    request.Status,
                    request.CorrelationId.ToString("D")))
                .Focus(state.GetFocusedRow(
                    DashboardSection.Requests,
                    requests.ActiveRequests.Count == 0
                        ? null
                        : requests.ActiveRequests[0].CorrelationId))
                .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.Requests, key))
                .Fill()
        ]).Fill();
    }

    private static VStackWidget BuildTraces(
        RootContext context,
        DashboardState state)
    {
        ControlTraceInfo trace = state.Snapshot.Requests.Trace;
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
                    ResizableHeader(header, state, DashboardSection.Traces, 0, "Request", 30),
                    ResizableHeader(header, state, DashboardSection.Traces, 1, "State", 12),
                    ResizableHeader(header, state, DashboardSection.Traces, 2, "Duration ms", 14),
                    header.Cell("Correlation").Width(SizeHint.Fill)
                ])
                .Row((row, entry, _) => BuildRowCells(
                    row,
                    state,
                    DashboardSection.Traces,
                    entry.Ordinal,
                    entry.Name,
                    entry.Status,
                    entry.DurationMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
                    entry.CorrelationId.ToString("D")))
                .Focus(state.GetFocusedRow(
                    DashboardSection.Traces,
                    trace.Entries.Count == 0
                        ? null
                        : trace.Entries[0].Ordinal))
                .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.Traces, key))
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
        DashboardState state) =>
        context.Table(state.Snapshot.BuildHosts)
            .RowKey(static host => host.ProcessId)
            .Header(header =>
            [
                ResizableHeader(header, state, DashboardSection.BuildHosts, 0, "PID", 10),
                ResizableHeader(header, state, DashboardSection.BuildHosts, 1, "Kind", 24),
                ResizableHeader(header, state, DashboardSection.BuildHosts, 2, "Workspaces", 12),
                header.Cell("Projects").Width(SizeHint.Fill)
            ])
            .Row((row, host, _) =>
            {
                int key = GetBuildHostKey(host);
                return BuildRowCells(
                    row,
                    state,
                    DashboardSection.BuildHosts,
                    key,
                    host.ProcessId.ToString(CultureInfo.InvariantCulture),
                    host.Kind,
                    host.WorkspaceCount.ToString(CultureInfo.InvariantCulture),
                    host.ProjectCount.ToString(CultureInfo.InvariantCulture));
            })
            .Focus(state.GetFocusedRow(
                DashboardSection.BuildHosts,
                state.Snapshot.BuildHosts.Count == 0
                    ? null
                    : state.Snapshot.BuildHosts[0].ProcessId))
            .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.BuildHosts, key))
            .Fill();

    private static TableWidget<ControlCacheInfo> BuildCaches(
        RootContext context,
        DashboardState state) =>
        context.Table(state.Snapshot.Caches)
            .RowKey(static cache => cache.Name)
            .Header(header =>
            [
                ResizableHeader(header, state, DashboardSection.Caches, 0, "Cache", 36),
                ResizableHeader(header, state, DashboardSection.Caches, 1, "Entries", 12),
                header.Cell("Capacity").Width(SizeHint.Fill)
            ])
            .Row((row, cache, _) => BuildRowCells(
                row,
                state,
                DashboardSection.Caches,
                cache.Name,
                cache.Name,
                cache.EntryCount.ToString(CultureInfo.InvariantCulture),
                cache.Capacity?.ToString(CultureInfo.InvariantCulture) ?? "dynamic"))
            .Focus(state.GetFocusedRow(
                DashboardSection.Caches,
                state.Snapshot.Caches.Count == 0
                    ? null
                    : state.Snapshot.Caches[0].Name))
            .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.Caches, key))
            .Fill();

    private static TableWidget<ControlLogEntry> BuildLogs(
        RootContext context,
        DashboardState state) =>
        context.Table(state.Snapshot.Logs)
            .RowKey(static entry => entry.Sequence)
            .Header(header =>
            [
                ResizableHeader(header, state, DashboardSection.Logs, 0, "Time", 12),
                ResizableHeader(header, state, DashboardSection.Logs, 1, "Level", 12),
                ResizableHeader(header, state, DashboardSection.Logs, 2, "Category", 30),
                header.Cell("Message").Width(SizeHint.Fill)
            ])
            .Row((row, entry, _) => BuildRowCells(
                row,
                state,
                DashboardSection.Logs,
                entry.Sequence,
                entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                entry.Level,
                entry.Category,
                entry.Message))
            .Focus(state.GetFocusedRow(
                DashboardSection.Logs,
                state.Snapshot.Logs.Count == 0
                    ? null
                    : state.Snapshot.Logs[0].Sequence))
            .OnFocusChanged(key => state.SetFocusedRow(DashboardSection.Logs, key))
            .Fill();

    private static int GetBuildHostKey(ControlBuildHostInfo host) => host.ProcessId;

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
