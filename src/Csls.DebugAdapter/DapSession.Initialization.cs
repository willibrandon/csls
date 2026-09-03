using Csls.DebugAdapter.Protocol;

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
                writer.WriteBoolean("supportsHitConditionalBreakpoints", true);
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
                writer.WriteBoolean("supportsVariablePaging", true);
                writer.WriteBoolean("supportsReadMemoryRequest", true);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }
}
