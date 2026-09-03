using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Globalization;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles bounded DAP target-memory reads.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask ReadMemoryAsync(
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
            JsonElement arguments = request.Arguments;
            string memoryReference = GetMemoryReference(arguments);
            long offset = GetMemoryOffset(arguments);
            int count = GetMemoryCount(arguments);
            DebugMemoryReadResult result = await _engineSession
                .ReadMemoryAsync(memoryReference, offset, count, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "address",
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"0x{result.Address:X}"));
                    writer.WriteString("data", Convert.ToBase64String(result.Data.Span));
                    if (result.UnreadableBytes != 0)
                    {
                        writer.WriteNumber("unreadableBytes", result.UnreadableBytes);
                    }

                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string GetMemoryReference(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("memoryReference", out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException(
                "The readMemory request requires a non-empty string memoryReference.");
        }

        return value.GetString()!;
    }

    private static long GetMemoryOffset(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("offset", out JsonElement value))
        {
            return 0;
        }

        if (!value.TryGetInt64(out long result))
        {
            throw new ArgumentException("The readMemory offset must be an integer.");
        }

        return result;
    }

    private static int GetMemoryCount(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("count", out JsonElement value) ||
            !value.TryGetInt32(out int result) ||
            result < 0)
        {
            throw new ArgumentException(
                "The readMemory request requires a non-negative integer count.");
        }

        return result;
    }
}
