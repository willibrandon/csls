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
            writeBody: static writer => WriteCapabilities(writer, dump: false),
            cancellationToken).ConfigureAwait(false);
    }
}
