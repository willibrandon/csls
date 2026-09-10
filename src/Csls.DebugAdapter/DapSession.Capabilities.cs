using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Publishes capabilities for the selected live or offline target.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask ConfigureTargetCapabilitiesAsync(bool dump, CancellationToken cancellationToken)
    {
        if (_dumpCapabilities == dump)
        {
            return;
        }

        _dumpCapabilities = dump;
        await _writer.WriteEventAsync("capabilities", writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("capabilities");
            WriteCapabilities(writer, dump);
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteCapabilities(Utf8JsonWriter writer, bool dump)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("supportsConfigurationDoneRequest", true);
        writer.WriteBoolean("supportsModulesRequest", true);
        writer.WriteBoolean("supportsLoadedSourcesRequest", !dump);
        writer.WriteBoolean("supportsBreakpointLocationsRequest", !dump);
        writer.WriteBoolean("supportsFunctionBreakpoints", !dump);
        writer.WriteBoolean("supportsConditionalBreakpoints", !dump);
        writer.WriteBoolean("supportsHitConditionalBreakpoints", !dump);
        writer.WriteBoolean("supportsLogPoints", !dump);
        writer.WriteBoolean("supportsInstructionBreakpoints", !dump);
        writer.WriteBoolean("supportsExceptionFilterOptions", !dump);
        writer.WriteStartArray("exceptionBreakpointFilters");
        if (!dump)
        {
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
        }
        writer.WriteEndArray();
        writer.WriteBoolean("supportsExceptionInfoRequest", !dump);
        writer.WriteBoolean("supportsEvaluateForHovers", !dump);
        writer.WriteBoolean("supportsCompletionsRequest", !dump);
        writer.WriteBoolean("supportsSetVariable", !dump);
        writer.WriteBoolean("supportsSetExpression", !dump);
        writer.WriteBoolean("supportsCancelRequest", true);
        writer.WriteBoolean("supportsReadMemoryRequest", !dump);
        writer.WriteBoolean("supportsDisassembleRequest", !dump);
        writer.WriteBoolean("supportsStepInTargetsRequest", !dump);
        writer.WriteBoolean("supportsGotoTargetsRequest", !dump);
        writer.WriteBoolean("supportsRestartRequest", !dump);
        writer.WriteEndObject();
    }
}
