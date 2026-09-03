using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles source breakpoint requests and DAP breakpoint serialization.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask SetBreakpointsAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state is not DapSessionState.Configuring and not DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            (string sourcePath, IReadOnlyList<DebugSourceBreakpointRequest> breakpoints) =
                ParseSourceBreakpoints(request.Arguments);
            IReadOnlyList<DebugSourceBreakpointInfo> results = await _engineSession
                .SetSourceBreakpointsAsync(sourcePath, breakpoints, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("breakpoints");
                    foreach (DebugSourceBreakpointInfo breakpoint in results)
                    {
                        WriteBreakpoint(writer, breakpoint);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private (string SourcePath, IReadOnlyList<DebugSourceBreakpointRequest> Breakpoints)
        ParseSourceBreakpoints(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("source", out JsonElement source) ||
            source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("path", out JsonElement pathValue) ||
            pathValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(pathValue.GetString()))
        {
            throw new ArgumentException(
                "The setBreakpoints request requires an absolute source.path.");
        }

        if (arguments.TryGetProperty("sourceModified", out JsonElement sourceModified) &&
            sourceModified.ValueKind == JsonValueKind.True)
        {
            throw new ArgumentException(
                "Breakpoints cannot be bound while the editor source differs from the saved file.");
        }

        var result = new List<DebugSourceBreakpointRequest>();
        if (arguments.TryGetProperty("breakpoints", out JsonElement breakpoints))
        {
            if (breakpoints.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("The setBreakpoints breakpoints value must be an array.");
            }

            foreach (JsonElement breakpoint in breakpoints.EnumerateArray())
            {
                if (breakpoint.ValueKind != JsonValueKind.Object ||
                    !breakpoint.TryGetProperty("line", out JsonElement lineValue) ||
                    !lineValue.TryGetInt32(out int line))
                {
                    throw new ArgumentException(
                        "Every source breakpoint requires an integer line.");
                }

                RejectUnsupportedBreakpointOption(breakpoint, "condition");
                RejectUnsupportedBreakpointOption(breakpoint, "logMessage");
                string? hitCondition = GetOptionalBreakpointString(
                    breakpoint,
                    "hitCondition");
                int? column = null;
                if (breakpoint.TryGetProperty("column", out JsonElement columnValue))
                {
                    if (!columnValue.TryGetInt32(out int parsedColumn))
                    {
                        throw new ArgumentException(
                            "A source breakpoint column must be an integer.");
                    }

                    column = parsedColumn;
                }

                result.Add(new DebugSourceBreakpointRequest(
                    FromClientLine(line, "setBreakpoints"),
                    column is null
                        ? null
                        : FromClientColumn(column.Value, "setBreakpoints"),
                    hitCondition));
            }
        }
        else if (arguments.TryGetProperty("lines", out JsonElement lines) &&
            lines.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement lineValue in lines.EnumerateArray())
            {
                if (!lineValue.TryGetInt32(out int line))
                {
                    throw new ArgumentException(
                        "Every setBreakpoints lines entry must be an integer.");
                }

                result.Add(new DebugSourceBreakpointRequest(
                    FromClientLine(line, "setBreakpoints"),
                    Column: null));
            }
        }

        return (pathValue.GetString()!, result);
    }

    private static void RejectUnsupportedBreakpointOption(
        JsonElement breakpoint,
        string propertyName)
    {
        if (breakpoint.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(value.GetString()))
        {
            throw new ArgumentException(
                $"Source breakpoint {propertyName} is not supported by this capability set.");
        }
    }

    private static string? GetOptionalBreakpointString(
        JsonElement breakpoint,
        string propertyName)
    {
        if (!breakpoint.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                $"Source breakpoint {propertyName} must be a string.");
        }

        return value.GetString();
    }

    private void WriteBreakpoint(
        Utf8JsonWriter writer,
        DebugSourceBreakpointInfo breakpoint)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", breakpoint.Id);
        writer.WriteBoolean("verified", breakpoint.Verified);
        writer.WriteStartObject("source");
        writer.WriteString("name", Path.GetFileName(breakpoint.SourcePath));
        writer.WriteString("path", breakpoint.SourcePath);
        writer.WriteEndObject();
        writer.WriteNumber("line", ToClientLine(breakpoint.Line));
        if (breakpoint.Column is not null)
        {
            writer.WriteNumber("column", ToClientColumn(breakpoint.Column.Value));
        }

        if (breakpoint.Message is not null)
        {
            writer.WriteString("message", breakpoint.Message);
        }

        writer.WriteEndObject();
    }
}
