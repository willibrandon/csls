using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Configures managed exception-stage and type breakpoints.
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
            DebugExceptionBreakpointSetRequest breakpoints =
                ParseExceptionBreakpoints(request.Arguments);
            await _engineSession.SetExceptionBreakpointsAsync(breakpoints, cancellationToken)
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

    private static DebugExceptionBreakpointSetRequest ParseExceptionBreakpoints(
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("filters", out JsonElement filters) ||
            filters.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "The setExceptionBreakpoints request requires a filters array.");
        }

        var result = new List<DebugExceptionBreakpointRequest>();
        foreach (JsonElement filter in filters.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Every exception filter must be a string.");
            }

            result.Add(new DebugExceptionBreakpointRequest(
                ParseExceptionBreakMode(filter.GetString()),
                []));
        }

        if (arguments.TryGetProperty("filterOptions", out JsonElement filterOptions))
        {
            ParseExceptionFilterOptions(filterOptions, result);
        }

        if (arguments.TryGetProperty("exceptionOptions", out JsonElement exceptionOptions) &&
            exceptionOptions.ValueKind == JsonValueKind.Array &&
            exceptionOptions.GetArrayLength() != 0)
        {
            throw new ArgumentException("Exception path options are not supported.");
        }

        return new DebugExceptionBreakpointSetRequest(result);
    }

    private static void ParseExceptionFilterOptions(
        JsonElement options,
        List<DebugExceptionBreakpointRequest> result)
    {
        if (options.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Exception filterOptions must be an array.");
        }

        foreach (JsonElement option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object ||
                !option.TryGetProperty("filterId", out JsonElement filterId) ||
                filterId.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Every exception filter option requires a filterId.");
            }

            if (option.TryGetProperty("mode", out _))
            {
                throw new ArgumentException("Exception breakpoint modes are not supported.");
            }

            IReadOnlyList<string> typeNames = option.TryGetProperty(
                "condition",
                out JsonElement condition)
                ? ParseExceptionTypeNames(condition)
                : [];
            result.Add(new DebugExceptionBreakpointRequest(
                ParseExceptionBreakMode(filterId.GetString()),
                typeNames));
        }
    }

    private static string[] ParseExceptionTypeNames(JsonElement condition)
    {
        if (condition.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("An exception filter condition must be a string.");
        }

        string? value = condition.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        string[] names = value.Split(',', StringSplitOptions.TrimEntries);
        if (names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Exception filter conditions must contain comma-separated type names.");
        }

        return names;
    }

    private static DebugExceptionBreakMode ParseExceptionBreakMode(string? filter) => filter switch
    {
        "all" => DebugExceptionBreakMode.Thrown,
        "user-unhandled" => DebugExceptionBreakMode.UserUnhandled,
        "unhandled" => DebugExceptionBreakMode.Unhandled,
        string value => throw new ArgumentException(
            $"The exception filter '{value}' is not supported."),
        _ => throw new ArgumentException("An exception filter cannot be null.")
    };

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
        writer.WriteBoolean("supportsCondition", true);
        writer.WriteString(
            "conditionDescription",
            "Comma-separated exact or base managed exception type names.");
        writer.WriteEndObject();
    }
}
