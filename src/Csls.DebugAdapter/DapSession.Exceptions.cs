using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles DAP managed exception policy and current exception inspection.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask SetExceptionBreakpointsAsync(
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
            HashSet<DebugExceptionBreakMode> breakModes =
                ParseExceptionBreakModes(request.Arguments);
            await _engineSession.SetExceptionBreakModesAsync(breakModes, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask WriteExceptionInfoAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            int threadId = GetRequiredInteger(request.Arguments, "threadId", "exceptionInfo");
            DebugExceptionInfo exception = await _engineSession
                .GetExceptionInfoAsync(threadId, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("exceptionId", exception.ExceptionId);
                    writer.WriteString("description", exception.Description);
                    writer.WriteString("breakMode", exception.BreakMode switch
                    {
                        DebugExceptionBreakMode.Thrown => "always",
                        DebugExceptionBreakMode.UserUnhandled => "userUnhandled",
                        DebugExceptionBreakMode.Unhandled => "unhandled",
                        _ => throw new InvalidOperationException(
                            "The debugger returned an unknown exception break mode.")
                    });
                    writer.WriteStartObject("details");
                    writer.WriteString("typeName", exception.ExceptionId);
                    writer.WriteString("message", exception.Description);
                    writer.WriteEndObject();
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

    private static HashSet<DebugExceptionBreakMode> ParseExceptionBreakModes(
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("filters", out JsonElement filters) ||
            filters.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "The setExceptionBreakpoints request requires a filters array.");
        }

        var result = new HashSet<DebugExceptionBreakMode>();
        foreach (JsonElement filter in filters.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Every exception filter must be a string.");
            }

            _ = result.Add(filter.GetString() switch
            {
                "all" => DebugExceptionBreakMode.Thrown,
                "user-unhandled" => DebugExceptionBreakMode.UserUnhandled,
                "unhandled" => DebugExceptionBreakMode.Unhandled,
                string value => throw new ArgumentException(
                    $"The exception filter '{value}' is not supported."),
                _ => throw new ArgumentException("An exception filter cannot be null.")
            });
        }

        return result;
    }

    private static void WriteExceptionBreakpointFilter(
        Utf8JsonWriter writer,
        string filter,
        string label,
        string description,
        bool defaultValue)
    {
        writer.WriteStartObject();
        writer.WriteString("filter", filter);
        writer.WriteString("label", label);
        writer.WriteString("description", description);
        writer.WriteBoolean("default", defaultValue);
        writer.WriteEndObject();
    }
}
