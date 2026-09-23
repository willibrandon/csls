using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles source-aware DAP Step Into target discovery.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteStepInTargetsAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            int frameId = GetRequiredInteger(
                request.Arguments,
                "frameId",
                "stepInTargets");
            IReadOnlyList<DebugStepTargetInfo> targets = await _engineSession
                .GetStepTargetsAsync(frameId, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer => WriteStepTargets(writer, targets),
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

    private void WriteStepTargets(
        Utf8JsonWriter writer,
        IReadOnlyList<DebugStepTargetInfo> targets)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("targets");
        foreach (DebugStepTargetInfo target in targets)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", target.Id);
            writer.WriteString("label", target.Label);
            WriteOptionalSourceRange(
                writer,
                target.Line,
                target.Column,
                target.EndLine,
                target.EndColumn);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void WriteOptionalSourceRange(
        Utf8JsonWriter writer,
        int? line,
        int? column,
        int? endLine,
        int? endColumn)
    {
        if (line is > 0)
        {
            writer.WriteNumber("line", ToClientLine(line.Value));
        }

        if (column is > 0)
        {
            writer.WriteNumber("column", ToClientColumn(column.Value));
        }

        if (endLine is > 0)
        {
            writer.WriteNumber("endLine", ToClientLine(endLine.Value));
        }

        if (endColumn is > 0)
        {
            writer.WriteNumber("endColumn", ToClientColumn(endColumn.Value));
        }
    }
}
