using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Translates source coordinates between DAP client and debugger conventions.
/// </summary>
internal sealed partial class DapSession
{
    private void ConfigureCoordinateSystem(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (arguments.TryGetProperty("linesStartAt1", out JsonElement lines) &&
            lines.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _clientLinesStartAtOne = lines.GetBoolean();
        }

        if (arguments.TryGetProperty("columnsStartAt1", out JsonElement columns) &&
            columns.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _clientColumnsStartAtOne = columns.GetBoolean();
        }
    }

    private int FromClientLine(int line, string requestName) => FromClientCoordinate(
        line,
        _clientLinesStartAtOne,
        "line",
        requestName);

    private int FromClientColumn(int column, string requestName) => FromClientCoordinate(
        column,
        _clientColumnsStartAtOne,
        "column",
        requestName);

    private int ToClientLine(int line) => _clientLinesStartAtOne ? line : checked(line - 1);

    private int ToClientColumn(int column) => _clientColumnsStartAtOne
        ? column
        : checked(column - 1);

    private static int FromClientCoordinate(
        int value,
        bool startsAtOne,
        string coordinateName,
        string requestName)
    {
        int minimum = startsAtOne ? 1 : 0;
        if (value < minimum)
        {
            throw new ArgumentException(
                $"The {requestName} {coordinateName} must be at least {minimum}.");
        }

        return startsAtOne ? value : checked(value + 1);
    }
}
