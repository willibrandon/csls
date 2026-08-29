using Csls.Control;
using Csls.Control.Contracts;
using Csls.Dashboard;
using Csls.Protocol;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using LspRange = Csls.Protocol.Range;

namespace Csls.Cli.Worker;

/// <summary>
/// Executes normalized CLI operations through the versioned control protocol.
/// </summary>
internal static class CliWorkerHost
{
    /// <summary>
    /// Executes one normalized launcher request and returns its process exit code.
    /// </summary>
    /// <param name="arguments">The normalized internal request arguments.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        bool writeJson = arguments.Count > 1 && bool.TryParse(arguments[^1], out bool json) && json;
        try
        {
            return arguments.Count == 0
                ? Fail("invalid-request", "The launcher supplied no CLI operation.", writeJson)
                : arguments[0] switch
                {
                    "sessions-list" => await ListSessionsAsync(
                        arguments,
                        writeJson,
                        cancellationToken)
                        .ConfigureAwait(false),
                    "sessions-show" => await ShowSessionAsync(arguments, writeJson, cancellationToken)
                        .ConfigureAwait(false),
                    "sessions-watch" => await SessionWatchCommandHost.RunAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "agent-init" => await AgentInitCommandHost.RunAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "dashboard" => await RunDashboardAsync(arguments, cancellationToken)
                        .ConfigureAwait(false),
                    "doctor" => await DoctorCommandHost.RunAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "workspace-operation" => await RunWorkspaceOperationAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "requests-list" => await ListRequestsAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "requests-cancel" => await CancelRequestAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "trace-operation" => await RunTraceOperationAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "query-hover" => await QueryHoverAsync(arguments, writeJson, cancellationToken)
                        .ConfigureAwait(false),
                    "query-diagnostics" => await QueryDiagnosticsAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "query-completion" => await QueryCompletionAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "query-navigation" => await QueryNavigationAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "query-document-symbols" => await QueryDocumentSymbolsAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "query-workspace-symbols" => await QueryWorkspaceSymbolsAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "query-signature-help" => await QuerySignatureHelpAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "edit-rename" => await PreviewRenameAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "edit-format" => await PreviewFormattingAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    "edit-code-action" => await PreviewCodeActionsAsync(
                        arguments,
                        writeJson,
                        cancellationToken).ConfigureAwait(false),
                    _ => Fail(
                        "invalid-request",
                        $"The launcher supplied an unknown CLI operation: {arguments[0]}",
                        writeJson)
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                InvalidOperationException or
                SocketException or
                ArgumentException)
        {
            return Fail("operation-failed", exception.Message, writeJson);
        }
    }

    private static async Task<int> ListSessionsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 4 ||
            !int.TryParse(
                arguments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit))
        {
            return Fail("invalid-request", "The launcher supplied an invalid session list.", writeJson);
        }

        IReadOnlyList<ControlSessionInfo> sessions = await ControlSessionDiscovery
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        CliPage<ControlSessionInfo> page = CliPagination.Create(
            sessions,
            "sessions-list",
            arguments[1],
            limit);
        CliOutputWriter.WriteSessions(page.Items, writeJson, page.NextCursor);
        return 0;
    }

    private static Task<int> RunDashboardAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 3 ||
            !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId))
        {
            throw new InvalidDataException(
                "The launcher supplied an invalid dashboard request.");
        }

        string? workspacePath = string.IsNullOrWhiteSpace(arguments[2])
            ? null
            : arguments[2];
        return DashboardHost.RunAsync(processId, workspacePath, cancellationToken);
    }

    private static async Task<int> RunWorkspaceOperationAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 5 ||
            !int.TryParse(
                arguments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid workspace operation.",
                writeJson);
        }

        string? workspacePath = string.IsNullOrWhiteSpace(arguments[3])
            ? null
            : arguments[3];
        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            workspacePath,
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        ControlWorkspaceOperationResult result = arguments[1] switch
        {
            "restore" => await session.Client.RestoreWorkspaceAsync(cancellationToken)
                .ConfigureAwait(false),
            "reload" => await session.Client.ReloadWorkspaceAsync(cancellationToken)
                .ConfigureAwait(false),
            "restart-build-host" => await session.Client.RestartBuildHostsAsync(cancellationToken)
                .ConfigureAwait(false),
            "clear-cache" => await session.Client.ClearCachesAsync(cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidDataException(
                $"The launcher supplied an unknown workspace operation: {arguments[1]}")
        };
        CliOutputWriter.WriteWorkspaceOperation(result, writeJson);
        return 0;
    }

    private static async Task<int> ListRequestsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 6 ||
            !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int limit))
        {
            return Fail("invalid-request", "The launcher supplied an invalid request list.", writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            string.IsNullOrWhiteSpace(arguments[2]) ? null : arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        ControlDashboardSnapshot dashboard = await session.Client.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = false },
            cancellationToken).ConfigureAwait(false);
        CliPage<ControlRequestInfo> page = CliPagination.Create(
            dashboard.Requests.ActiveRequests,
            "requests-list",
            arguments[3],
            limit);
        CliOutputWriter.WriteRequests(
            CreateRequestPage(dashboard.Requests, page.Items),
            writeJson,
            page.NextCursor);
        return 0;
    }

    private static async Task<int> CancelRequestAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 5 ||
            !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId) ||
            !Guid.TryParseExact(arguments[3], "D", out Guid correlationId))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid request cancellation.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            string.IsNullOrWhiteSpace(arguments[2]) ? null : arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        ControlCancelRequestResult result = await session.Client.CancelRequestAsync(
            new ControlCancelRequest { CorrelationId = correlationId },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteRequestCancellation(result, writeJson);
        return result.CancellationRequested ? 0 : 1;
    }

    private static async Task<int> RunTraceOperationAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 5 ||
            !int.TryParse(
                arguments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId))
        {
            return Fail("invalid-request", "The launcher supplied an invalid trace request.", writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            string.IsNullOrWhiteSpace(arguments[3]) ? null : arguments[3],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        ControlTraceInfo trace = arguments[1] switch
        {
            "start" => await session.Client.StartTraceAsync(cancellationToken).ConfigureAwait(false),
            "stop" => await session.Client.StopTraceAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidDataException(
                $"The launcher supplied an unknown trace operation: {arguments[1]}")
        };
        CliOutputWriter.WriteTrace(trace, writeJson);
        return 0;
    }

    private static async Task<int> ShowSessionAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 3 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
        {
            return Fail("invalid-request", "The launcher supplied an invalid session request.", writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        CliOutputWriter.WriteSession(session, writeJson);
        return 0;
    }

    private static async Task<int> QueryHoverAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 7 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out int character))
        {
            return Fail("invalid-request", "The launcher supplied an invalid hover request.", writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        ControlHoverResult hover = await session.Client.GetHoverAsync(
            new ControlHoverRequest
            {
                DocumentPath = arguments[3],
                Position = new Position(line, character)
            },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteHover(hover, writeJson);
        return 0;
    }

    private static async Task<int> QueryDiagnosticsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 8 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid diagnostic request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        DocumentDiagnosticReport report = await session.Client.GetDiagnosticsAsync(
            new ControlDiagnosticRequest
            {
                DocumentPath = arguments[3],
                PreviousResultId = string.IsNullOrEmpty(arguments[4]) ? null : arguments[4]
            },
            cancellationToken).ConfigureAwait(false);
        CliPage<Diagnostic> page = CliPagination.Create(
            report.Items ?? [],
            "query-diagnostics",
            arguments[5],
            limit);
        CliOutputWriter.WriteDiagnostics(
            report with { Items = page.Items },
            writeJson,
            page.NextCursor);
        return 0;
    }

    private static async Task<int> QueryCompletionAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 9 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out int character) ||
            !int.TryParse(arguments[7], NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid completion request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        CompletionList completion = await session.Client.GetCompletionAsync(
            new ControlCompletionRequest
            {
                DocumentPath = arguments[3],
                Position = new Position(line, character)
            },
            cancellationToken).ConfigureAwait(false);
        CliPage<CompletionItem> page = CliPagination.Create(
            completion.Items,
            "query-completion",
            arguments[6],
            limit);
        CliOutputWriter.WriteCompletion(
            completion with
            {
                IsIncomplete = completion.IsIncomplete || page.NextCursor is not null,
                Items = page.Items
            },
            writeJson,
            page.NextCursor);
        return 0;
    }

    private static async Task<int> QueryNavigationAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 11 ||
            arguments[1] is not (
                "definition" or
                "declaration" or
                "type-definition" or
                "implementation" or
                "selection-range" or
                "highlights" or
                "references") ||
            !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
            !int.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out int character) ||
            !bool.TryParse(arguments[7], out bool includeDeclaration) ||
            !int.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid navigation request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[3],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        var request = new ControlNavigationRequest
        {
            DocumentPath = arguments[4],
            Position = new Position(line, character),
            IncludeDeclaration = includeDeclaration
        };
        if (string.Equals(arguments[1], "selection-range", StringComparison.Ordinal))
        {
            IReadOnlyList<SelectionRange> ranges = await session.Client.GetSelectionRangesAsync(
                new ControlSelectionRangeRequest
                {
                    DocumentPath = arguments[4],
                    Positions = [new Position(line, character)]
                },
                cancellationToken).ConfigureAwait(false);
            CliPage<SelectionRange> page = CliPagination.Create(
                ranges,
                "query-selection-range",
                arguments[8],
                limit);
            CliOutputWriter.WriteSelectionRanges(page.Items, writeJson, page.NextCursor);
            return 0;
        }

        if (string.Equals(arguments[1], "highlights", StringComparison.Ordinal))
        {
            IReadOnlyList<DocumentHighlight> highlights = await session.Client
                .GetDocumentHighlightsAsync(request, cancellationToken)
                .ConfigureAwait(false);
            CliPage<DocumentHighlight> page = CliPagination.Create(
                highlights,
                "query-highlights",
                arguments[8],
                limit);
            CliOutputWriter.WriteDocumentHighlights(page.Items, writeJson, page.NextCursor);
            return 0;
        }

        IReadOnlyList<Location> locations = arguments[1] switch
        {
            "definition" => await session.Client.GetDefinitionAsync(request, cancellationToken)
                .ConfigureAwait(false),
            "declaration" => await session.Client.GetDeclarationAsync(request, cancellationToken)
                .ConfigureAwait(false),
            "type-definition" => await session.Client.GetTypeDefinitionAsync(request, cancellationToken)
                .ConfigureAwait(false),
            "implementation" => await session.Client.GetImplementationAsync(request, cancellationToken)
                .ConfigureAwait(false),
            _ => await session.Client.GetReferencesAsync(request, cancellationToken)
                .ConfigureAwait(false)
        };
        CliPage<Location> locationPage = CliPagination.Create(
            locations,
            string.Concat("query-", arguments[1]),
            arguments[8],
            limit);
        CliOutputWriter.WriteLocations(
            locationPage.Items,
            writeJson,
            locationPage.NextCursor);
        return 0;
    }

    private static async Task<int> QueryDocumentSymbolsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 7 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture,
                out int limit))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid document symbol request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        IReadOnlyList<DocumentSymbol> symbols = await session.Client.GetDocumentSymbolsAsync(
            new ControlDocumentRequest { DocumentPath = arguments[3] },
            cancellationToken).ConfigureAwait(false);
        CliPage<DocumentSymbol> page = CliPagination.Create(
            symbols,
            "query-document-symbols",
            arguments[4],
            limit);
        CliOutputWriter.WriteDocumentSymbols(page.Items, writeJson, page.NextCursor);
        return 0;
    }

    private static async Task<int> QueryWorkspaceSymbolsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 7 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture,
                out int limit))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid workspace symbol request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        IReadOnlyList<WorkspaceSymbol> symbols = await session.Client.GetWorkspaceSymbolsAsync(
            new ControlWorkspaceSymbolRequest { Query = arguments[3] },
            cancellationToken).ConfigureAwait(false);
        CliPage<WorkspaceSymbol> page = CliPagination.Create(
            symbols,
            "query-workspace-symbols",
            arguments[4],
            limit);
        CliOutputWriter.WriteWorkspaceSymbols(page.Items, writeJson, page.NextCursor);
        return 0;
    }

    private static async Task<int> QuerySignatureHelpAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 7 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture,
                out int line) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture,
                out int character))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid signature help request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        SignatureHelp? signatureHelp = await session.Client.GetSignatureHelpAsync(
            new ControlSignatureHelpRequest
            {
                DocumentPath = arguments[3],
                Position = new Position(line, character)
            },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteSignatureHelp(signatureHelp, writeJson);
        return 0;
    }

    private static async Task<int> PreviewRenameAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 9 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture,
                out int line) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture,
                out int character) ||
            !bool.TryParse(arguments[7], out bool apply))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid rename preview request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        ControlEditPlan plan = await session.Client.PreviewRenameAsync(
            new ControlRenameRequest
            {
                DocumentPath = arguments[3],
                Position = new Position(line, character),
                NewName = arguments[6]
            },
            cancellationToken).ConfigureAwait(false);
        if (apply)
        {
            ControlApplyEditPlanResult result = await session.Client.ApplyEditPlanAsync(
                new ControlApplyEditPlanRequest { PlanId = plan.PlanId },
                cancellationToken).ConfigureAwait(false);
            CliOutputWriter.WriteAppliedEditPlan(result, writeJson);
        }
        else
        {
            CliOutputWriter.WriteEditPlan(plan, writeJson);
        }

        return 0;
    }

    private static async Task<int> PreviewFormattingAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 8 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture,
                out int tabSize) ||
            !bool.TryParse(arguments[5], out bool insertSpaces) ||
            !bool.TryParse(arguments[6], out bool apply))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid formatting preview request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        ControlEditPlan plan = await session.Client.PreviewFormattingAsync(
            new ControlFormattingRequest
            {
                DocumentPath = arguments[3],
                Options = new FormattingOptions
                {
                    TabSize = tabSize,
                    InsertSpaces = insertSpaces,
                    TrimTrailingWhitespace = true,
                    InsertFinalNewline = true,
                    TrimFinalNewlines = true
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (apply)
        {
            ControlApplyEditPlanResult result = await session.Client.ApplyEditPlanAsync(
                new ControlApplyEditPlanRequest { PlanId = plan.PlanId },
                cancellationToken).ConfigureAwait(false);
            CliOutputWriter.WriteAppliedEditPlan(result, writeJson);
        }
        else
        {
            CliOutputWriter.WriteEditPlan(plan, writeJson);
        }

        return 0;
    }

    private static async Task<int> PreviewCodeActionsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 12 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture,
                out int line) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture,
                out int character) ||
            line < 0 ||
            character < 0 ||
            !int.TryParse(arguments[9], NumberStyles.None, CultureInfo.InvariantCulture,
                out int limit) ||
            !bool.TryParse(arguments[10], out bool apply))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid code-action preview request.",
                writeJson);
        }

        CliControlSession session = await CliControlSession.ConnectAsync(
            processId,
            arguments[2],
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sessionCleanup = session.ConfigureAwait(false);
        IReadOnlyList<ControlCodeActionPlan> actions = await session.Client.GetCodeActionsAsync(
            new ControlCodeActionRequest
            {
                DocumentPath = arguments[3],
                Range = new LspRange(
                    new Position(line, character),
                    new Position(line, character)),
                Only = [arguments[6]]
            },
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ControlCodeActionPlan> matchingActions = string.IsNullOrEmpty(arguments[7])
            ? actions
            :
            [
                .. actions.Where(action => string.Equals(
                    action.Action.Title,
                    arguments[7],
                    StringComparison.Ordinal))
            ];
        CliPage<ControlCodeActionPlan> page = CliPagination.Create(
            matchingActions,
            "edit-code-action",
            arguments[8],
            limit);
        if (apply)
        {
            ControlCodeActionPlan action = page.Items.Count == 1
                ? page.Items[0]
                : throw new InvalidOperationException(
                    "Applying a code action requires exactly one matching action; " +
                    "use --title to select its exact Roslyn title.");
            ControlEditPlan editPlan = action.EditPlan
                ?? throw new InvalidOperationException(
                    "The selected code action does not contain a source edit plan.");
            ControlApplyEditPlanResult result = await session.Client.ApplyEditPlanAsync(
                new ControlApplyEditPlanRequest { PlanId = editPlan.PlanId },
                cancellationToken).ConfigureAwait(false);
            CliOutputWriter.WriteAppliedEditPlan(result, writeJson);
        }
        else
        {
            CliOutputWriter.WriteCodeActionPlans(
                page.Items,
                writeJson,
                page.NextCursor);
        }

        return 0;
    }

    private static async Task<ControlSessionInfo> ResolveSessionAsync(
        int processId,
        CancellationToken cancellationToken) =>
        await ControlSessionDiscovery.ResolveAsync(
            processId,
            workspacePath: null,
            cancellationToken).ConfigureAwait(false);

    private static ControlRequestSchedulerInfo CreateRequestPage(
        ControlRequestSchedulerInfo source,
        IReadOnlyList<ControlRequestInfo> activeRequests) =>
        new()
        {
            ActivityCapacity = source.ActivityCapacity,
            Capacity = source.Capacity,
            ForegroundConcurrency = source.ForegroundConcurrency,
            BackgroundConcurrency = source.BackgroundConcurrency,
            AcceptedRequests = source.AcceptedRequests,
            CompletedRequests = source.CompletedRequests,
            QueuedRequests = source.QueuedRequests,
            ActiveForegroundRequests = source.ActiveForegroundRequests,
            ActiveBackgroundRequests = source.ActiveBackgroundRequests,
            IsMutationActive = source.IsMutationActive,
            IsStopping = source.IsStopping,
            TotalActiveRequests = source.TotalActiveRequests,
            ActiveRequestsTruncated = source.ActiveRequestsTruncated ||
                activeRequests.Count < source.ActiveRequests.Count,
            ActiveRequests = activeRequests,
            Trace = source.Trace
        };

    private static int Fail(string code, string message, bool writeJson)
    {
        CliOutputWriter.WriteError(code, message, writeJson);
        return 1;
    }
}
