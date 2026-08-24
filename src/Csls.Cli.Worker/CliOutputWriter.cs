using Csls.Control.Contracts;
using Csls.Protocol;
using System.Globalization;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Cli.Worker;

/// <summary>
/// Writes human-readable terminal results or stable source-generated JSON envelopes.
/// </summary>
internal static class CliOutputWriter
{
    /// <summary>
    /// Writes a bounded list of live sessions.
    /// </summary>
    /// <param name="sessions">The live sessions.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteSessions(
        IReadOnlyList<ControlSessionInfo> sessions,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                sessions,
                typeof(IReadOnlyList<ControlSessionInfo>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        if (sessions.Count == 0)
        {
            Console.Out.WriteLine("No live csls sessions.");
            return;
        }

        Console.Out.WriteLine("PID\tSTATE\tGENERATION\tWORKSPACES");
        foreach (ControlSessionInfo session in sessions)
        {
            Console.Out.WriteLine(
                $"{session.ProcessId}\t{session.LifecycleState}\t{session.WorkspaceGeneration}\t{string.Join(';', session.WorkspaceRoots)}");
        }
    }

    /// <summary>
    /// Writes the detailed state for one live session.
    /// </summary>
    /// <param name="session">The selected live session.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteSession(ControlSessionInfo session, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                session,
                typeof(ControlSessionInfo),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        Console.Out.WriteLine($"Process: {session.ProcessId}");
        Console.Out.WriteLine($"State: {session.LifecycleState}");
        Console.Out.WriteLine($"Generation: {session.WorkspaceGeneration}");
        Console.Out.WriteLine($"Socket: {session.SocketPath}");
        Console.Out.WriteLine("Workspaces:");
        foreach (string root in session.WorkspaceRoots)
        {
            Console.Out.WriteLine($"  {root}");
        }
    }

    /// <summary>
    /// Writes one ordered live-session watch observation.
    /// </summary>
    /// <param name="watchEvent">The changed session and complete current snapshot.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteSessionWatchEvent(
        SessionWatchEvent watchEvent,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                watchEvent,
                CliJsonSerializerContext.Default.SessionWatchEvent);
            WriteEnvelope(success: true, data);
            return;
        }

        if (watchEvent.Kind == SessionWatchEventKind.Snapshot)
        {
            Console.Out.WriteLine(
                $"SNAPSHOT {watchEvent.Sessions.Count.ToString(CultureInfo.InvariantCulture)} live session(s)");
            return;
        }

        ControlSessionInfo session = watchEvent.Session
            ?? throw new InvalidDataException(
                $"The {watchEvent.Kind} watch event did not include a session.");
        Console.Out.WriteLine(
            $"{watchEvent.Kind.ToString().ToUpperInvariant()} " +
            $"{session.ProcessId.ToString(CultureInfo.InvariantCulture)} " +
            $"{session.LifecycleState} " +
            $"{session.WorkspaceGeneration.ToString(CultureInfo.InvariantCulture)} " +
            string.Join(';', session.WorkspaceRoots));
    }

    /// <summary>
    /// Writes the observable result of one completed workspace maintenance operation.
    /// </summary>
    /// <param name="result">The completed workspace operation result.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteWorkspaceOperation(
        ControlWorkspaceOperationResult result,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                result,
                typeof(ControlWorkspaceOperationResult),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        Console.Out.WriteLine($"Operation: {result.Operation}");
        Console.Out.WriteLine(
            $"Generation: {result.PreviousGeneration} -> {result.CurrentGeneration}");
        Console.Out.WriteLine($"Workspaces: {result.AffectedWorkspaceCount}");
        Console.Out.WriteLine($"Restored entry points: {result.RestoredEntryPointCount}");
        Console.Out.WriteLine($"Restarted build hosts: {result.RestartedBuildHostCount}");
        Console.Out.WriteLine($"Cleared cache entries: {result.ClearedCacheEntryCount}");
    }

    /// <summary>
    /// Writes bounded scheduler activity and current tracing state.
    /// </summary>
    /// <param name="requests">The live scheduler observation.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteRequests(
        ControlRequestSchedulerInfo requests,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                requests,
                CliJsonSerializerContext.Default.ControlRequestSchedulerInfo);
            WriteEnvelope(success: true, data);
            return;
        }

        Console.Out.WriteLine(
            $"Trace: {(requests.Trace.IsActive ? "active" : "inactive")}; " +
            $"retained={requests.Trace.Entries.Count}; dropped={requests.Trace.DroppedEntries}");
        if (requests.ActiveRequests.Count == 0)
        {
            Console.Out.WriteLine("No queued or running requests.");
            return;
        }

        Console.Out.WriteLine("ORDINAL\tCORRELATION\tSTATUS\tMODE\tGENERATION\tNAME");
        foreach (ControlRequestInfo request in requests.ActiveRequests)
        {
            Console.Out.WriteLine(
                $"{request.Ordinal}\t{request.CorrelationId:D}\t{request.Status}\t" +
                $"{request.Mode}\t{request.WorkspaceGeneration?.ToString(CultureInfo.InvariantCulture) ?? "queued"}\t{request.Name}");
        }
    }

    /// <summary>
    /// Writes the deterministic result of one request cancellation attempt.
    /// </summary>
    /// <param name="result">The request cancellation result.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteRequestCancellation(
        ControlCancelRequestResult result,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                result,
                CliJsonSerializerContext.Default.ControlCancelRequestResult);
            WriteEnvelope(result.CancellationRequested, data);
            return;
        }

        Console.Out.WriteLine(
            result.CancellationRequested
                ? $"Cancellation requested for {result.CorrelationId:D}."
                : $"No live request has correlation ID {result.CorrelationId:D}.");
    }

    /// <summary>
    /// Writes the active or stopped bounded request lifecycle trace.
    /// </summary>
    /// <param name="trace">The request trace observation.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteTrace(ControlTraceInfo trace, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                trace,
                CliJsonSerializerContext.Default.ControlTraceInfo);
            WriteEnvelope(success: true, data);
            return;
        }

        Console.Out.WriteLine($"Trace: {trace.TraceId?.ToString("D") ?? "none"}");
        Console.Out.WriteLine($"State: {(trace.IsActive ? "active" : "stopped")}");
        Console.Out.WriteLine($"Entries: {trace.Entries.Count}");
        Console.Out.WriteLine($"Dropped: {trace.DroppedEntries}");
        foreach (ControlTraceEntry entry in trace.Entries)
        {
            Console.Out.WriteLine(
                $"{entry.CorrelationId:D}\t{entry.Status}\t" +
                $"{entry.DurationMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} ms\t{entry.Name}");
        }
    }

    /// <summary>
    /// Writes hover information returned by the shared control service.
    /// </summary>
    /// <param name="hover">The hover result.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteHover(ControlHoverResult hover, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(hover);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                hover,
                typeof(ControlHoverResult),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        Console.Out.WriteLine(
            hover.Found
                ? hover.Hover!.Contents.Value
                : "No hover information found.");
    }

    /// <summary>
    /// Writes compiler and analyzer diagnostics returned by the shared control service.
    /// </summary>
    /// <param name="report">The complete or unchanged diagnostic report.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteDiagnostics(
        DocumentDiagnosticReport report,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                report,
                typeof(DocumentDiagnosticReport),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        if (string.Equals(report.Kind, "unchanged", StringComparison.Ordinal))
        {
            Console.Out.WriteLine($"Diagnostics unchanged ({report.ResultId}).");
            return;
        }

        IReadOnlyList<Diagnostic> diagnostics = report.Items ?? [];
        if (diagnostics.Count == 0)
        {
            Console.Out.WriteLine("No diagnostics.");
            return;
        }

        foreach (Diagnostic diagnostic in diagnostics)
        {
            Console.Out.WriteLine(
                $"{diagnostic.Range.Start.Line + 1}:{diagnostic.Range.Start.Character + 1} " +
                $"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    /// <summary>
    /// Writes bounded Roslyn completion candidates returned by the shared control service.
    /// </summary>
    /// <param name="completion">The ordered completion list.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteCompletion(CompletionList completion, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                completion,
                typeof(CompletionList),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        foreach (CompletionItem item in completion.Items)
        {
            Console.Out.WriteLine(
                string.IsNullOrWhiteSpace(item.Detail)
                    ? item.Label
                    : $"{item.Label}\t{item.Detail}");
        }
    }

    /// <summary>
    /// Writes bounded source navigation locations returned by the shared control service.
    /// </summary>
    /// <param name="locations">The ordered source locations.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteLocations(IReadOnlyList<Location> locations, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(locations);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                locations,
                typeof(IReadOnlyList<Location>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        foreach (Location location in locations)
        {
            Console.Out.WriteLine(
                $"{location.Uri}:{location.Range.Start.Line + 1}:{location.Range.Start.Character + 1}");
        }
    }

    /// <summary>
    /// Writes nested syntax selections returned by the shared control service.
    /// </summary>
    /// <param name="ranges">The ordered selection hierarchies.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteSelectionRanges(
        IReadOnlyList<SelectionRange> ranges,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                ranges,
                typeof(IReadOnlyList<SelectionRange>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        foreach (SelectionRange range in ranges)
        {
            Console.Out.WriteLine(
                $"{range.Range.Start.Line + 1}:{range.Range.Start.Character + 1}-" +
                $"{range.Range.End.Line + 1}:{range.Range.End.Character + 1}");
        }
    }

    /// <summary>
    /// Writes semantic symbol highlights returned by the shared control service.
    /// </summary>
    /// <param name="highlights">The ordered document highlights.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteDocumentHighlights(
        IReadOnlyList<DocumentHighlight> highlights,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(highlights);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                highlights,
                typeof(IReadOnlyList<DocumentHighlight>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        foreach (DocumentHighlight highlight in highlights)
        {
            Console.Out.WriteLine(
                $"{highlight.Kind}\t{highlight.Range.Start.Line + 1}:" +
                $"{highlight.Range.Start.Character + 1}");
        }
    }

    /// <summary>
    /// Writes hierarchical source declarations returned by the shared control service.
    /// </summary>
    /// <param name="symbols">The source declaration hierarchy.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteDocumentSymbols(
        IReadOnlyList<DocumentSymbol> symbols,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                symbols,
                typeof(IReadOnlyList<DocumentSymbol>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        WriteDocumentSymbols(symbols, depth: 0);
    }

    /// <summary>
    /// Writes bounded workspace declaration search results from the shared control service.
    /// </summary>
    /// <param name="symbols">The resolved workspace symbols.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteWorkspaceSymbols(
        IReadOnlyList<WorkspaceSymbol> symbols,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                symbols,
                typeof(IReadOnlyList<WorkspaceSymbol>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        foreach (WorkspaceSymbol symbol in symbols)
        {
            string location = symbol.Location.Range is LspRange range
                ? $"{symbol.Location.Uri}:{range.Start.Line + 1}:{range.Start.Character + 1}"
                : symbol.Location.Uri.ToString();
            Console.Out.WriteLine(
                $"{symbol.Kind}\t{symbol.Name}\t{symbol.ContainerName}\t{location}");
        }
    }

    /// <summary>
    /// Writes overload-aware signature help returned by the shared control service.
    /// </summary>
    /// <param name="signatureHelp">The optional active signature help.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteSignatureHelp(SignatureHelp? signatureHelp, bool writeJson)
    {
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                signatureHelp,
                typeof(SignatureHelp),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        if (signatureHelp is null)
        {
            Console.Out.WriteLine("No signature help found.");
            return;
        }

        for (int index = 0; index < signatureHelp.Signatures.Count; index++)
        {
            string marker = index == signatureHelp.ActiveSignature ? "*" : " ";
            Console.Out.WriteLine($"{marker} {signatureHelp.Signatures[index].Label}");
        }
    }

    /// <summary>
    /// Writes a one-use edit plan and every version and content-hash precondition.
    /// </summary>
    /// <param name="plan">The complete one-use edit plan.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteEditPlan(ControlEditPlan plan, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                plan,
                CliJsonSerializerContext.Default.ControlEditPlan);
            WriteEnvelope(success: true, data);
            return;
        }

        Console.Out.WriteLine($"Plan: {plan.PlanId:D}");
        Console.Out.WriteLine($"Operation: {plan.Operation}");
        Console.Out.WriteLine($"Expires: {plan.ExpiresAtUtc:O}");
        foreach (TextDocumentEdit documentEdit in plan.Edit.DocumentChanges)
        {
            Console.Out.WriteLine(
                $"{documentEdit.TextDocument.Uri}\tversion=" +
                $"{documentEdit.TextDocument.Version?.ToString(CultureInfo.InvariantCulture) ?? "closed"}");
            WriteTextEdits(documentEdit.Edits);
        }
    }

    /// <summary>
    /// Writes the result of an explicitly applied one-use edit plan.
    /// </summary>
    /// <param name="result">The new generation and changed document paths.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteAppliedEditPlan(
        ControlApplyEditPlanResult result,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                result,
                CliJsonSerializerContext.Default.ControlApplyEditPlanResult);
            WriteEnvelope(success: true, data);
            return;
        }

        Console.Out.WriteLine($"Applied generation {result.WorkspaceGeneration}.");
        foreach (string documentPath in result.DocumentPaths)
        {
            Console.Out.WriteLine(documentPath);
        }
    }

    /// <summary>
    /// Writes bounded text edit previews for one source document.
    /// </summary>
    /// <param name="edits">The non-overlapping source edits.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteTextEdits(IReadOnlyList<TextEdit> edits, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                edits,
                typeof(IReadOnlyList<TextEdit>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        WriteTextEdits(edits);
    }

    /// <summary>
    /// Writes concrete code actions and their optional one-use application plans.
    /// </summary>
    /// <param name="actions">The supported concrete code action plans.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteCodeActionPlans(
        IReadOnlyList<ControlCodeActionPlan> actions,
        bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                actions,
                typeof(IReadOnlyList<ControlCodeActionPlan>),
                CliJsonSerializerContext.Default);
            WriteEnvelope(success: true, data);
            return;
        }

        foreach (ControlCodeActionPlan action in actions)
        {
            Console.Out.WriteLine($"{action.Action.Kind}\t{action.Action.Title}");
        }
    }

    /// <summary>
    /// Writes the complete result of one real workspace doctor inspection.
    /// </summary>
    /// <param name="report">The ordered checks and observed workspace state.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteDoctor(DoctorReport report, bool writeJson)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (writeJson)
        {
            JsonElement data = JsonSerializer.SerializeToElement(
                report,
                CliJsonSerializerContext.Default.DoctorReport);
            WriteEnvelope(report.IsHealthy, data);
            return;
        }

        Console.Out.WriteLine($"Workspace: {report.WorkspacePath}");
        Console.Out.WriteLine($".NET SDK: {report.SdkVersion ?? "unavailable"}");
        Console.Out.WriteLine(
            $"Roslyn: {report.Projects.Count} project(s), {report.DocumentCount} document(s)");
        Console.Out.WriteLine($"Diagnostics: {report.TotalDiagnostics}");
        foreach (DoctorCheck check in report.Checks)
        {
            Console.Out.WriteLine($"{check.Status.ToString().ToUpperInvariant()} {check.Name}: {check.Message}");
        }
    }

    /// <summary>
    /// Writes an actionable command failure to the requested output channel.
    /// </summary>
    /// <param name="code">The stable failure category.</param>
    /// <param name="message">The actionable failure description.</param>
    /// <param name="writeJson">Whether to write a machine-readable envelope.</param>
    internal static void WriteError(string code, string message, bool writeJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!writeJson)
        {
            Console.Error.WriteLine(message);
            return;
        }

        JsonElement data = JsonSerializer.SerializeToElement(
            new CliError { Code = code, Message = message },
            CliJsonSerializerContext.Default.CliError);
        WriteEnvelope(success: false, data);
    }

    private static void WriteEnvelope(bool success, JsonElement data)
    {
        var envelope = new CliResponseEnvelope
        {
            CorrelationId = Guid.NewGuid().ToString("D"),
            Success = success,
            Data = data,
            NextCursor = null
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(
            envelope,
            CliJsonSerializerContext.Default.CliResponseEnvelope));
    }

    private static void WriteDocumentSymbols(
        IReadOnlyList<DocumentSymbol> symbols,
        int depth)
    {
        foreach (DocumentSymbol symbol in symbols)
        {
            Console.Out.WriteLine($"{new string(' ', depth * 2)}{symbol.Kind}\t{symbol.Name}");
            if (symbol.Children is { Count: > 0 } children)
            {
                WriteDocumentSymbols(children, depth + 1);
            }
        }
    }

    private static void WriteTextEdits(IReadOnlyList<TextEdit> edits)
    {
        foreach (TextEdit edit in edits)
        {
            string newText = JsonSerializer.Serialize(
                edit.NewText,
                CliJsonSerializerContext.Default.String);
            Console.Out.WriteLine(
                $"  {edit.Range.Start.Line}:{edit.Range.Start.Character}-" +
                $"{edit.Range.End.Line}:{edit.Range.End.Character}\t{newText}");
        }
    }
}
