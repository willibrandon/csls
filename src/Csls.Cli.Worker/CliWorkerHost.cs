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
                    "sessions-list" => await ListSessionsAsync(writeJson, cancellationToken)
                        .ConfigureAwait(false),
                    "sessions-show" => await ShowSessionAsync(arguments, writeJson, cancellationToken)
                        .ConfigureAwait(false),
                    "sessions-watch" => await SessionWatchCommandHost.RunAsync(
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
        bool writeJson,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ControlSessionInfo> sessions = await ControlSessionDiscovery
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        CliOutputWriter.WriteSessions(sessions, writeJson);
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
        ControlSessionInfo session = await ControlSessionDiscovery.ResolveAsync(
            processId,
            workspacePath,
            cancellationToken).ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlWorkspaceOperationResult result = arguments[1] switch
        {
            "restore" => await client.RestoreWorkspaceAsync(cancellationToken).ConfigureAwait(false),
            "reload" => await client.ReloadWorkspaceAsync(cancellationToken).ConfigureAwait(false),
            "restart-build-host" => await client.RestartBuildHostsAsync(cancellationToken)
                .ConfigureAwait(false),
            "clear-cache" => await client.ClearCachesAsync(cancellationToken).ConfigureAwait(false),
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
        if (arguments.Count != 4 ||
            !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId))
        {
            return Fail("invalid-request", "The launcher supplied an invalid request list.", writeJson);
        }

        ControlSessionInfo session = await ControlSessionDiscovery.ResolveAsync(
            processId,
            string.IsNullOrWhiteSpace(arguments[2]) ? null : arguments[2],
            cancellationToken).ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlDashboardSnapshot dashboard = await client.GetDashboardSnapshotAsync(
            new ControlDashboardRequest { IncludeDiagnostics = false },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteRequests(dashboard.Requests, writeJson);
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

        ControlSessionInfo session = await ControlSessionDiscovery.ResolveAsync(
            processId,
            string.IsNullOrWhiteSpace(arguments[2]) ? null : arguments[2],
            cancellationToken).ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlCancelRequestResult result = await client.CancelRequestAsync(
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

        ControlSessionInfo session = await ControlSessionDiscovery.ResolveAsync(
            processId,
            string.IsNullOrWhiteSpace(arguments[3]) ? null : arguments[3],
            cancellationToken).ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlTraceInfo trace = arguments[1] switch
        {
            "start" => await client.StartTraceAsync(cancellationToken).ConfigureAwait(false),
            "stop" => await client.StopTraceAsync(cancellationToken).ConfigureAwait(false),
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
        if (arguments.Count != 6 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int character))
        {
            return Fail("invalid-request", "The launcher supplied an invalid hover request.", writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlHoverResult hover = await client.GetHoverAsync(
            new ControlHoverRequest
            {
                DocumentPath = arguments[2],
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
        if (arguments.Count != 5 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid diagnostic request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        DocumentDiagnosticReport report = await client.GetDiagnosticsAsync(
            new ControlDiagnosticRequest
            {
                DocumentPath = arguments[2],
                PreviousResultId = string.IsNullOrEmpty(arguments[3]) ? null : arguments[3]
            },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteDiagnostics(report, writeJson);
        return 0;
    }

    private static async Task<int> QueryCompletionAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 6 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int character))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid completion request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        CompletionList completion = await client.GetCompletionAsync(
            new ControlCompletionRequest
            {
                DocumentPath = arguments[2],
                Position = new Position(line, character)
            },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteCompletion(completion, writeJson);
        return 0;
    }

    private static async Task<int> QueryNavigationAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 8 ||
            arguments[1] is not (
                "definition" or
                "declaration" or
                "type-definition" or
                "implementation" or
                "selection-range" or
                "highlights" or
                "references") ||
            !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int line) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out int character) ||
            !bool.TryParse(arguments[6], out bool includeDeclaration))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid navigation request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        var request = new ControlNavigationRequest
        {
            DocumentPath = arguments[3],
            Position = new Position(line, character),
            IncludeDeclaration = includeDeclaration
        };
        if (string.Equals(arguments[1], "selection-range", StringComparison.Ordinal))
        {
            IReadOnlyList<SelectionRange> ranges = await client.GetSelectionRangesAsync(
                new ControlSelectionRangeRequest
                {
                    DocumentPath = arguments[3],
                    Positions = [new Position(line, character)]
                },
                cancellationToken).ConfigureAwait(false);
            CliOutputWriter.WriteSelectionRanges(ranges, writeJson);
            return 0;
        }

        if (string.Equals(arguments[1], "highlights", StringComparison.Ordinal))
        {
            IReadOnlyList<DocumentHighlight> highlights = await client
                .GetDocumentHighlightsAsync(request, cancellationToken)
                .ConfigureAwait(false);
            CliOutputWriter.WriteDocumentHighlights(highlights, writeJson);
            return 0;
        }

        IReadOnlyList<Location> locations = arguments[1] switch
        {
            "definition" => await client.GetDefinitionAsync(request, cancellationToken)
                .ConfigureAwait(false),
            "declaration" => await client.GetDeclarationAsync(request, cancellationToken)
                .ConfigureAwait(false),
            "type-definition" => await client.GetTypeDefinitionAsync(request, cancellationToken)
                .ConfigureAwait(false),
            "implementation" => await client.GetImplementationAsync(request, cancellationToken)
                .ConfigureAwait(false),
            _ => await client.GetReferencesAsync(request, cancellationToken)
                .ConfigureAwait(false)
        };
        CliOutputWriter.WriteLocations(locations, writeJson);
        return 0;
    }

    private static async Task<int> QueryDocumentSymbolsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 4 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid document symbol request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        IReadOnlyList<DocumentSymbol> symbols = await client.GetDocumentSymbolsAsync(
            new ControlDocumentRequest { DocumentPath = arguments[2] },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteDocumentSymbols(symbols, writeJson);
        return 0;
    }

    private static async Task<int> QueryWorkspaceSymbolsAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 4 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid workspace symbol request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        IReadOnlyList<WorkspaceSymbol> symbols = await client.GetWorkspaceSymbolsAsync(
            new ControlWorkspaceSymbolRequest { Query = arguments[2] },
            cancellationToken).ConfigureAwait(false);
        CliOutputWriter.WriteWorkspaceSymbols(symbols, writeJson);
        return 0;
    }

    private static async Task<int> QuerySignatureHelpAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 6 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture,
                out int line) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture,
                out int character))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid signature help request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        SignatureHelp? signatureHelp = await client.GetSignatureHelpAsync(
            new ControlSignatureHelpRequest
            {
                DocumentPath = arguments[2],
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
        if (arguments.Count != 8 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture,
                out int line) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture,
                out int character) ||
            !bool.TryParse(arguments[6], out bool apply))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid rename preview request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlEditPlan plan = await client.PreviewRenameAsync(
            new ControlRenameRequest
            {
                DocumentPath = arguments[2],
                Position = new Position(line, character),
                NewName = arguments[5]
            },
            cancellationToken).ConfigureAwait(false);
        if (apply)
        {
            ControlApplyEditPlanResult result = await client.ApplyEditPlanAsync(
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
        if (arguments.Count != 7 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture,
                out int tabSize) ||
            !bool.TryParse(arguments[4], out bool insertSpaces) ||
            !bool.TryParse(arguments[5], out bool apply))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid formatting preview request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        ControlEditPlan plan = await client.PreviewFormattingAsync(
            new ControlFormattingRequest
            {
                DocumentPath = arguments[2],
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
            ControlApplyEditPlanResult result = await client.ApplyEditPlanAsync(
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
        if (arguments.Count != 6 ||
            !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture,
                out int processId) ||
            !bool.TryParse(arguments[4], out bool apply))
        {
            return Fail(
                "invalid-request",
                "The launcher supplied an invalid code-action preview request.",
                writeJson);
        }

        ControlSessionInfo session = await ResolveSessionAsync(processId, cancellationToken)
            .ConfigureAwait(false);
        var client = new ControlRpcClient(session.SocketPath);
        await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
        IReadOnlyList<ControlCodeActionPlan> actions = await client.GetCodeActionsAsync(
            new ControlCodeActionRequest
            {
                DocumentPath = arguments[2],
                Range = new LspRange(new Position(0, 0), new Position(0, 0)),
                Only = [arguments[3]]
            },
            cancellationToken).ConfigureAwait(false);
        if (apply)
        {
            ControlCodeActionPlan action = actions.Count == 1
                ? actions[0]
                : throw new InvalidOperationException(
                    "Applying a code action requires exactly one matching action.");
            ControlEditPlan editPlan = action.EditPlan
                ?? throw new InvalidOperationException(
                    "The selected code action does not contain a source edit plan.");
            ControlApplyEditPlanResult result = await client.ApplyEditPlanAsync(
                new ControlApplyEditPlanRequest { PlanId = editPlan.PlanId },
                cancellationToken).ConfigureAwait(false);
            CliOutputWriter.WriteAppliedEditPlan(result, writeJson);
        }
        else
        {
            CliOutputWriter.WriteCodeActionPlans(actions, writeJson);
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

    private static int Fail(string code, string message, bool writeJson)
    {
        CliOutputWriter.WriteError(code, message, writeJson);
        return 1;
    }
}
