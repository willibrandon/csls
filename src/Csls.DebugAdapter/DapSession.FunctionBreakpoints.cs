using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles managed function-breakpoint requests and serialization.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask SetFunctionBreakpointsAsync(
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
            IReadOnlyList<DebugFunctionBreakpointRequest> breakpoints =
                ParseFunctionBreakpoints(request.Arguments);
            IReadOnlyList<DebugFunctionBreakpointInfo> results = await _engineSession
                .SetFunctionBreakpointsAsync(breakpoints, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("breakpoints");
                    foreach (DebugFunctionBreakpointInfo breakpoint in results)
                    {
                        WriteFunctionBreakpoint(writer, breakpoint);
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

    private static List<DebugFunctionBreakpointRequest> ParseFunctionBreakpoints(
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("breakpoints", out JsonElement breakpoints) ||
            breakpoints.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "The setFunctionBreakpoints request requires a breakpoints array.");
        }

        var result = new List<DebugFunctionBreakpointRequest>();
        foreach (JsonElement breakpoint in breakpoints.EnumerateArray())
        {
            if (breakpoint.ValueKind != JsonValueKind.Object ||
                !breakpoint.TryGetProperty("name", out JsonElement nameValue) ||
                nameValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameValue.GetString()))
            {
                throw new ArgumentException(
                    "Every function breakpoint requires a non-empty name.");
            }

            string? condition = GetOptionalFunctionBreakpointString(
                breakpoint,
                "condition");
            string? hitCondition = GetOptionalFunctionBreakpointString(
                breakpoint,
                "hitCondition");
            result.Add(new DebugFunctionBreakpointRequest(
                nameValue.GetString()!,
                condition,
                hitCondition));
        }

        return result;
    }

    private static string? GetOptionalFunctionBreakpointString(
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
                $"Function breakpoint {propertyName} must be a string.");
        }

        return value.GetString();
    }

    private static void WriteFunctionBreakpoint(
        Utf8JsonWriter writer,
        DebugFunctionBreakpointInfo breakpoint)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", breakpoint.Id);
        writer.WriteBoolean("verified", breakpoint.Verified);
        if (breakpoint.Message is not null)
        {
            writer.WriteString("message", breakpoint.Message);
        }

        writer.WriteEndObject();
    }
}
