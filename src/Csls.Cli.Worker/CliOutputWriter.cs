using System.Text.Json;
using Csls.Control.Contracts;
using Csls.Protocol;

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
}
