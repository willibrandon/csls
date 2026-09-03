using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles DAP source-document and executable-location discovery.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteLoadedSourcesAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (!CanInspectSymbols())
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            IReadOnlyList<DebugSourceInfo> sources = await _engineSession
                .GetLoadedSourcesAsync(cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("sources");
                    foreach (DebugSourceInfo source in sources)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", source.Name);
                        writer.WriteString("path", source.Path);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or
                UnauthorizedAccessException or BadImageFormatException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask WriteBreakpointLocationsAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (!CanInspectSymbols())
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            BreakpointLocationRange range = ParseBreakpointLocationRange(request.Arguments);
            IReadOnlyList<DebugBreakpointLocation> locations = await _engineSession
                .GetBreakpointLocationsAsync(
                    range.SourcePath,
                    range.StartLine,
                    range.StartColumn,
                    range.EndLine,
                    range.EndColumn,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteBreakpointLocationsResponseAsync(request, locations, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException or
                IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private BreakpointLocationRange ParseBreakpointLocationRange(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("source", out JsonElement source) ||
            source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("path", out JsonElement path) ||
            path.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(path.GetString()) ||
            !arguments.TryGetProperty("line", out JsonElement lineValue) ||
            !lineValue.TryGetInt32(out int clientLine))
        {
            throw new ArgumentException(
                "The breakpointLocations request requires an absolute source.path and integer line.");
        }

        string sourcePath = path.GetString()!;
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException(
                "The breakpointLocations source.path must be absolute.");
        }

        int startLine = FromClientLine(clientLine, "breakpointLocations");
        int startColumn = ReadOptionalCoordinate(
            arguments,
            "column",
            defaultValue: 1,
            FromClientColumn);
        int endLine = ReadOptionalCoordinate(
            arguments,
            "endLine",
            defaultValue: startLine,
            FromClientLine);
        int endColumn = ReadOptionalCoordinate(
            arguments,
            "endColumn",
            defaultValue: int.MaxValue,
            FromClientColumn);
        return new BreakpointLocationRange(
            Path.GetFullPath(sourcePath),
            startLine,
            startColumn,
            endLine,
            endColumn);
    }

    private static int ReadOptionalCoordinate(
        JsonElement arguments,
        string propertyName,
        int defaultValue,
        Func<int, string, int> convert)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value))
        {
            return defaultValue;
        }

        if (!value.TryGetInt32(out int parsed))
        {
            throw new ArgumentException(
                $"The breakpointLocations {propertyName} must be an integer.");
        }

        return convert(parsed, "breakpointLocations");
    }

    private ValueTask WriteBreakpointLocationsResponseAsync(
        Request request,
        IReadOnlyList<DebugBreakpointLocation> locations,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("breakpoints");
                foreach (DebugBreakpointLocation location in locations)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("line", ToClientLine(location.Line));
                    writer.WriteNumber("column", ToClientColumn(location.Column));
                    writer.WriteNumber("endLine", ToClientLine(location.EndLine));
                    writer.WriteNumber("endColumn", ToClientColumn(location.EndColumn));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);

    private bool CanInspectSymbols() =>
        _state is DapSessionState.Running or DapSessionState.Stopped;

}
