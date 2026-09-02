using Csls.DebugAdapter.Protocol;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles attach configuration for a Debug Adapter Protocol session.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask PrepareAttachAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Initialized)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            _pendingAttachProcessId = DapAttachOptionsParser.Parse(request.Arguments);
        }
        catch (ArgumentException exception)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _pendingTargetRequest = request;
        _startMethod = "attach";
        _terminateDebuggeeByDefault = false;
        _state = DapSessionState.Configuring;
        await _writer.WriteEventAsync(
            "initialized",
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
    }
}
