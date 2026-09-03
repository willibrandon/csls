using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles safe DAP managed instruction-pointer movement.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteGotoTargetsAsync(
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
            DebugGotoTargetsRequest targetRequest = await ParseGotoTargetsAsync(
                request.Arguments,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DebugGotoTargetInfo> targets = await _engineSession
                .GetGotoTargetsAsync(targetRequest, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer => WriteGotoTargets(writer, targets),
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

    private async ValueTask GotoAsync(Request request, CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var arguments = new DebugGotoRequest(
                GetRequiredInteger(request.Arguments, "threadId", "goto"),
                GetRequiredInteger(request.Arguments, "targetId", "goto"));
            _deferGotoStoppedEvent = true;
            await _engineSession.GotoAsync(arguments, cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await FlushDeferredGotoStopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _deferGotoStoppedEvent = false;
            _deferredStop = null;
        }
    }

    private async ValueTask<DebugGotoTargetsRequest> ParseGotoTargetsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (_stoppedThreadId is not int threadId)
        {
            throw new InvalidOperationException(
                "Goto targets require a stopped managed thread.");
        }

        string path = GetSourcePath(arguments, "gotoTargets");
        int line = FromClientLine(
            GetRequiredInteger(arguments, "line", "gotoTargets"),
            "gotoTargets");
        int? column = arguments.TryGetProperty("column", out JsonElement columnValue)
            ? columnValue.TryGetInt32(out int parsedColumn)
                ? FromClientColumn(parsedColumn, "gotoTargets")
                : throw new ArgumentException("The gotoTargets column must be an integer.")
            : null;
        DebugStackTrace stack = await _engineSession.GetStackTraceAsync(
            threadId,
            startFrame: 0,
            levels: 1,
            cancellationToken).ConfigureAwait(false);
        DebugStackFrameInfo frame = stack.StackFrames.SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Managed thread {threadId} has no active managed frame.");
        return new DebugGotoTargetsRequest(frame.Id, path, line, column);
    }

    private static string GetSourcePath(JsonElement arguments, string requestName)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("source", out JsonElement source) ||
            source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("path", out JsonElement path) ||
            path.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(path.GetString()))
        {
            throw new ArgumentException(
                $"The {requestName} request requires an absolute source.path.");
        }

        return path.GetString()!;
    }

    private void WriteGotoTargets(
        Utf8JsonWriter writer,
        IReadOnlyList<DebugGotoTargetInfo> targets)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("targets");
        foreach (DebugGotoTargetInfo target in targets)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", target.Id);
            writer.WriteString("label", target.Label);
            writer.WriteNumber("line", ToClientLine(target.Line));
            writer.WriteNumber("column", ToClientColumn(target.Column));
            writer.WriteNumber("endLine", ToClientLine(target.EndLine));
            writer.WriteNumber("endColumn", ToClientColumn(target.EndColumn));
            writer.WriteString("instructionPointerReference", target.InstructionReference);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private async ValueTask FlushDeferredGotoStopAsync(CancellationToken cancellationToken)
    {
        if (_deferredStop is not { } stop)
        {
            throw new InvalidOperationException("The debugger did not publish the goto stop.");
        }

        await WriteStoppedEventAsync(
            stop.Reason,
            stop.ThreadId,
            stop.Exception,
            cancellationToken).ConfigureAwait(false);
    }
}
