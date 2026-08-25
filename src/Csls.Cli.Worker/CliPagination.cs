using System.Globalization;
using System.Text;

namespace Csls.Cli.Worker;

/// <summary>
/// Creates deterministic bounded CLI pages from operation-scoped opaque cursors.
/// </summary>
internal static class CliPagination
{
    private const int MaximumCursorLength = 512;
    private const int MaximumLimit = 200;

    /// <summary>
    /// Returns one validated result page for the supplied operation.
    /// </summary>
    /// <typeparam name="T">The paginated item type.</typeparam>
    /// <param name="items">The complete bounded source result.</param>
    /// <param name="operation">The stable operation identifier bound to the cursor.</param>
    /// <param name="cursor">The optional opaque continuation cursor.</param>
    /// <param name="limit">The maximum requested page size.</param>
    /// <returns>The selected items and optional next cursor.</returns>
    internal static CliPage<T> Create<T>(
        IReadOnlyList<T> items,
        string operation,
        string? cursor,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"The page limit must be between 1 and {MaximumLimit}.");
        }

        int offset = DecodeCursor(cursor, operation);
        if (offset > items.Count)
        {
            throw new InvalidDataException(
                "The continuation cursor is beyond the current result set.");
        }

        int count = Math.Min(limit, items.Count - offset);
        var pageItems = new T[count];
        for (int index = 0; index < count; index++)
        {
            pageItems[index] = items[offset + index];
        }

        int nextOffset = offset + count;
        return new CliPage<T>
        {
            Items = pageItems,
            NextCursor = nextOffset < items.Count
                ? EncodeCursor(operation, nextOffset)
                : null
        };
    }

    private static int DecodeCursor(string? cursor, string operation)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        if (cursor.Length > MaximumCursorLength)
        {
            throw new InvalidDataException(
                $"The continuation cursor cannot exceed {MaximumCursorLength} characters.");
        }

        try
        {
            string padded = cursor
                .Replace('-', '+')
                .Replace('_', '/');
            padded = (padded.Length % 4) switch
            {
                0 => padded,
                2 => string.Concat(padded, "=="),
                3 => string.Concat(padded, "="),
                _ => throw new FormatException("The cursor has invalid Base64 padding.")
            };
            string value = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            string[] parts = value.Split(':');
            if (parts.Length != 3 ||
                !string.Equals(parts[0], "1", StringComparison.Ordinal) ||
                !string.Equals(parts[1], operation, StringComparison.Ordinal) ||
                !int.TryParse(
                    parts[2],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int offset) ||
                offset < 0)
            {
                throw new FormatException("The cursor payload is invalid.");
            }

            return offset;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException(
                "The continuation cursor is invalid for this operation.",
                exception);
        }
    }

    private static string EncodeCursor(string operation, int offset)
    {
        string value = string.Create(
            CultureInfo.InvariantCulture,
            $"1:{operation}:{offset}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
