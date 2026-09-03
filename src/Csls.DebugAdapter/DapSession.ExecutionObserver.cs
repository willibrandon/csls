using Csls.Debugger.Contracts;

namespace Csls.DebugAdapter;

/// <summary>
/// Publishes protocol-neutral debugger events as DAP notifications.
/// </summary>
internal sealed partial class DapSession
{
    /// <inheritdoc />
    public async ValueTask OnOutputAsync(
        DebugOutputCategory category,
        string output,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "output",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "category",
                        category == DebugOutputCategory.StandardOutput ? "stdout" : "stderr");
                    writer.WriteString("output", output);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnContinuedAsync(CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        _state = DapSessionState.Running;
        _stoppedThreadId = null;
        try
        {
            await _writer.WriteEventAsync(
                "continued",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("allThreadsContinued", true);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }
}
