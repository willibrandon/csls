using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Serves runtime-backed debugger expression completions for stopped frames.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteCompletionsAsync(
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
            (string line, int cursor) = ParseCompletionLine(request.Arguments);
            int frameId = await GetEvaluationFrameIdAsync(
                request.Arguments,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<DebugCompletionInfo> completions = await _engineSession
                .GetCompletionsAsync(
                    frameId,
                    line,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("targets");
                    foreach (DebugCompletionInfo completion in completions)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("label", completion.Label);
                        writer.WriteString("text", completion.Text);
                        writer.WriteString("detail", completion.Detail);
                        writer.WriteString("type", CompletionType(completion.Kind));
                        writer.WriteNumber(
                            "start",
                            ToClientColumn(checked(completion.ReplacementStart + 1)));
                        writer.WriteNumber("length", completion.ReplacementLength);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private (string Line, int Cursor) ParseCompletionLine(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                "The completions request requires a text string.");
        }

        string text = textElement.GetString()!;
        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        int lineIndex = 0;
        if (arguments.TryGetProperty("line", out JsonElement lineElement))
        {
            if (!lineElement.TryGetInt32(out int clientLine))
            {
                throw new ArgumentException(
                    "The completions line must be an integer.");
            }

            lineIndex = checked(FromClientLine(clientLine, "completions") - 1);
        }

        if ((uint)lineIndex >= (uint)lines.Length)
        {
            throw new ArgumentException(
                "The completions line is outside the supplied text.");
        }

        if (!arguments.TryGetProperty("column", out JsonElement columnElement) ||
            !columnElement.TryGetInt32(out int clientColumn))
        {
            throw new ArgumentException(
                "The completions request requires an integer column.");
        }

        int cursor = checked(FromClientColumn(clientColumn, "completions") - 1);
        if ((uint)cursor > (uint)lines[lineIndex].Length)
        {
            throw new ArgumentException(
                "The completions column is outside the selected line.");
        }

        return (lines[lineIndex], cursor);
    }

    private static string CompletionType(DebugCompletionItemKind kind) => kind switch
    {
        DebugCompletionItemKind.Variable => "variable",
        DebugCompletionItemKind.Field => "field",
        DebugCompletionItemKind.Method => "method",
        DebugCompletionItemKind.Keyword => "keyword",
        _ => "text"
    };
}
