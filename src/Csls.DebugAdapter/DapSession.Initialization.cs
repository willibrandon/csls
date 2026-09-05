using Csls.DebugAdapter.Protocol;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Negotiates DAP client coordinates and debugger capabilities.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask InitializeAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Created)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        ConfigureCoordinateSystem(request.Arguments);
        _clientSupportsVariablePaging = request.Arguments.ValueKind == JsonValueKind.Object &&
            request.Arguments.TryGetProperty("supportsVariablePaging", out JsonElement paging) &&
            paging.ValueKind == JsonValueKind.True;
        _clientSupportsInvalidatedEvent = request.Arguments.ValueKind == JsonValueKind.Object &&
            request.Arguments.TryGetProperty("supportsInvalidatedEvent", out JsonElement invalidated) &&
            invalidated.ValueKind == JsonValueKind.True;
        _state = DapSessionState.Initialized;
        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: static writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("supportsConfigurationDoneRequest", true);
                writer.WriteBoolean("supportsModulesRequest", true);
                writer.WriteBoolean("supportsLoadedSourcesRequest", true);
                writer.WriteBoolean("supportsBreakpointLocationsRequest", true);
                writer.WriteBoolean("supportsFunctionBreakpoints", true);
                writer.WriteBoolean("supportsConditionalBreakpoints", true);
                writer.WriteBoolean("supportsHitConditionalBreakpoints", true);
                writer.WriteBoolean("supportsLogPoints", true);
                writer.WriteBoolean("supportsInstructionBreakpoints", true);
                writer.WriteBoolean("supportsExceptionFilterOptions", true);
                writer.WriteStartArray("exceptionBreakpointFilters");
                WriteExceptionBreakpointFilter(
                    writer,
                    "all",
                    "Thrown Exceptions",
                    "Break when any managed exception is thrown.",
                    defaultValue: false);
                WriteExceptionBreakpointFilter(
                    writer,
                    "user-unhandled",
                    "User-Unhandled Exceptions",
                    "Break when a managed exception escapes user code.",
                    defaultValue: false);
                WriteExceptionBreakpointFilter(
                    writer,
                    "unhandled",
                    "Unhandled Exceptions",
                    "Break when a managed exception has no runtime handler.",
                    defaultValue: true);
                writer.WriteEndArray();
                writer.WriteBoolean("supportsExceptionInfoRequest", true);
                writer.WriteBoolean("supportsEvaluateForHovers", true);
                writer.WriteBoolean("supportsCompletionsRequest", true);
                writer.WriteBoolean("supportsSetVariable", true);
                writer.WriteBoolean("supportsSetExpression", true);
                writer.WriteBoolean("supportsCancelRequest", true);
                writer.WriteBoolean("supportsReadMemoryRequest", true);
                writer.WriteBoolean("supportsDisassembleRequest", true);
                writer.WriteBoolean("supportsStepInTargetsRequest", true);
                writer.WriteBoolean("supportsGotoTargetsRequest", true);
                writer.WriteBoolean("supportsRestartRequest", true);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }
}
