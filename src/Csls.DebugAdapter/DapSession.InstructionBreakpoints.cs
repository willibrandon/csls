using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles generation-safe managed-IL instruction breakpoints.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask SetInstructionBreakpointsAsync(
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
            IReadOnlyList<DebugInstructionBreakpointRequest> breakpoints =
                ParseInstructionBreakpoints(request.Arguments);
            IReadOnlyList<DebugInstructionBreakpointInfo> results = await _engineSession
                .SetInstructionBreakpointsAsync(breakpoints, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer => WriteInstructionBreakpoints(writer, results),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException or
            OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static List<DebugInstructionBreakpointRequest>
        ParseInstructionBreakpoints(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("breakpoints", out JsonElement breakpoints) ||
            breakpoints.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                "The setInstructionBreakpoints request requires a breakpoints array.");
        }

        var result = new List<DebugInstructionBreakpointRequest>();
        foreach (JsonElement breakpoint in breakpoints.EnumerateArray())
        {
            if (breakpoint.ValueKind != JsonValueKind.Object ||
                !breakpoint.TryGetProperty(
                    "instructionReference",
                    out JsonElement referenceValue) ||
                referenceValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(referenceValue.GetString()))
            {
                throw new ArgumentException(
                    "Every instruction breakpoint requires a non-empty instructionReference.");
            }

            RejectUnsupportedInstructionBreakpointOption(breakpoint, "mode");
            long offset = 0;
            if (breakpoint.TryGetProperty("offset", out JsonElement offsetValue) &&
                !offsetValue.TryGetInt64(out offset))
            {
                throw new ArgumentException(
                    "An instruction breakpoint offset must be an integer.");
            }

            result.Add(new DebugInstructionBreakpointRequest(
                referenceValue.GetString()!,
                offset,
                GetOptionalBreakpointString(breakpoint, "condition"),
                GetOptionalBreakpointString(breakpoint, "hitCondition")));
        }

        return result;
    }

    private static void RejectUnsupportedInstructionBreakpointOption(
        JsonElement breakpoint,
        string propertyName)
    {
        if (breakpoint.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(value.GetString()))
        {
            throw new ArgumentException(
                $"Instruction breakpoint {propertyName} is not supported by this capability set.");
        }
    }

    private static void WriteInstructionBreakpoints(
        Utf8JsonWriter writer,
        IReadOnlyList<DebugInstructionBreakpointInfo> breakpoints)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("breakpoints");
        foreach (DebugInstructionBreakpointInfo breakpoint in breakpoints)
        {
            WriteInstructionBreakpoint(writer, breakpoint);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteInstructionBreakpoint(
        Utf8JsonWriter writer,
        DebugInstructionBreakpointInfo breakpoint)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", breakpoint.Id);
        writer.WriteBoolean("verified", breakpoint.Verified);
        writer.WriteString("instructionReference", breakpoint.InstructionReference);
        writer.WriteNumber("offset", breakpoint.Offset);
        if (breakpoint.Message is not null)
        {
            writer.WriteString("message", breakpoint.Message);
            writer.WriteString(
                "reason",
                breakpoint.Message.Contains("pending", StringComparison.OrdinalIgnoreCase)
                    ? "pending"
                    : "failed");
        }

        writer.WriteEndObject();
    }
}
