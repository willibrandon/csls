using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles DAP retrieval of session-local source content.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteSourceContentAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (!CanInspectSymbols())
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            int sourceReference = GetRequiredSourceReference(request.Arguments);
            DebugSourceContent source = await _engineSession
                .GetSourceContentAsync(sourceReference, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("content", source.Content);
                    writer.WriteString("mimeType", source.MimeType);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                KeyNotFoundException or OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static int GetRequiredSourceReference(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("sourceReference", out JsonElement reference) ||
            !reference.TryGetInt32(out int value) ||
            value <= 0)
        {
            throw new ArgumentException(
                "The source request requires a positive integer sourceReference.");
        }

        return value;
    }
}
