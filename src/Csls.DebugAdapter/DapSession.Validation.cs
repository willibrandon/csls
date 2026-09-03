using Csls.DebugAdapter.Protocol;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Validates common DAP arguments and writes bounded request failures.
/// </summary>
internal sealed partial class DapSession
{
    private static int GetOptionalNonNegativeInteger(
        JsonElement arguments,
        string propertyName,
        string requestName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        if (!value.TryGetInt32(out int result) || result < 0)
        {
            throw new ArgumentException(
                $"The {requestName} {propertyName} value must be a non-negative integer.");
        }

        return result;
    }

    private static int GetRequiredInteger(
        JsonElement arguments,
        string propertyName,
        string requestName)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(propertyName, out JsonElement value) ||
            !value.TryGetInt32(out int result))
        {
            throw new ArgumentException(
                $"The {requestName} request requires an integer {propertyName}.");
        }

        return result;
    }

    private ValueTask WriteRequestFailureAsync(
        Request request,
        string message,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: false,
            message,
            writeBody: null,
            cancellationToken);

    private ValueTask WriteStateFailureAsync(
        Request request,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: false,
            $"The request '{request.Command}' is invalid while the session is {_state}.",
            writeBody: null,
            cancellationToken);
}
