using Csls.Debugger.Contracts;

namespace Csls.DebugAdapter;

/// <summary>
/// Publishes protocol-neutral debugger events as DAP notifications.
/// </summary>
internal sealed partial class DapSession
{
    /// <inheritdoc />
    public async ValueTask OnBreakpointChangedAsync(
        DebugSourceBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "breakpoint",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", "changed");
                    writer.WritePropertyName("breakpoint");
                    WriteBreakpoint(writer, breakpoint);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
            return;
        }
    }

    /// <inheritdoc />
    public async ValueTask OnFunctionBreakpointChangedAsync(
        DebugFunctionBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "breakpoint",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", "changed");
                    writer.WritePropertyName("breakpoint");
                    WriteFunctionBreakpoint(writer, breakpoint);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
            return;
        }
    }

    /// <inheritdoc />
    public async ValueTask OnInstructionBreakpointChangedAsync(
        DebugInstructionBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "breakpoint",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", "changed");
                    writer.WritePropertyName("breakpoint");
                    WriteInstructionBreakpoint(writer, breakpoint);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
            return;
        }
    }
}
