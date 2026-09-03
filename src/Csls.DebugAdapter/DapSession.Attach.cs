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
            _pendingAttach = DapAttachOptionsParser.Parse(request.Arguments);
            await _engineSession.ConfigureRuntimeOptionsAsync(
                _pendingAttach.JustMyCode,
                _pendingAttach.EnableStepFiltering,
                cancellationToken).ConfigureAwait(false);
            await _engineSession.ConfigureSourceOptionsAsync(
                DapSourceOptionsParser.ParseSourceFileMap(request.Arguments),
                DapSourceOptionsParser.ParseSourceLinkOptions(request.Arguments),
                cancellationToken).ConfigureAwait(false);
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
