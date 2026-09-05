using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded UTF-8 launch environment files without interpreting shell expressions.
/// </summary>
internal static class DebuggerEnvironmentFile
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding s_encoding = new(false, true);

    /// <summary>
    /// Reads assignments from a concrete environment file with platform environment-name comparison.
    /// </summary>
    /// <param name="path">The absolute environment-file path.</param>
    /// <param name="cancellationToken">Cancels file reads.</param>
    /// <returns>The parsed environment assignments.</returns>
    internal static async Task<Dictionary<string, string>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream = DebuggerInputFile.OpenRead(path);
        await using ConfiguredAsyncDisposable cleanup = stream.ConfigureAwait(false);
        if (!stream.CanSeek || stream.Length > MaximumBytes)
        {
            throw new InvalidDataException($"envFile must be a seekable file of at most {MaximumBytes} bytes.");
        }

        var bytes = new ArrayBufferWriter<byte>();
        while (true)
        {
            int capacity = Math.Min(4096, MaximumBytes + 1 - bytes.WrittenCount);
            int read = await stream.ReadAsync(bytes.GetMemory(capacity)[..capacity], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            bytes.Advance(read);
            if (bytes.WrittenCount > MaximumBytes)
            {
                throw new InvalidDataException($"envFile exceeds {MaximumBytes} bytes.");
            }
        }

        string content;
        try
        {
            content = s_encoding.GetString(bytes.WrittenSpan);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("envFile must contain valid UTF-8 text.");
        }

        return Parse(content, path);
    }

    private static Dictionary<string, string> Parse(string content, string path)
    {
        var result = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        using var reader = new StringReader(content.TrimStart('\uFEFF'));
        int lineNumber = 0;
        while (reader.ReadLine() is string line)
        {
            lineNumber++;
            string assignment = line.TrimStart();
            if (assignment.Length == 0 || assignment[0] == '#')
            {
                continue;
            }

            if (assignment.StartsWith("export", StringComparison.Ordinal) &&
                assignment.Length > 6 && char.IsWhiteSpace(assignment[6]))
            {
                assignment = assignment[6..].TrimStart();
            }

            int separator = assignment.IndexOf('=', StringComparison.Ordinal);
            if (separator < 1)
            {
                throw InvalidAssignment(path, lineNumber);
            }

            string name = assignment[..separator].Trim();
            if (name.Length == 0 || name.Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '.' or '-')))
            {
                throw InvalidAssignment(path, lineNumber);
            }

            string value = assignment[(separator + 1)..].TrimStart();
            int declarationLine = lineNumber;
            if (value.Length > 0 && value[0] is '\'' or '"')
            {
                value = ReadQuotedValue(value, reader, path, ref lineNumber);
            }
            else
            {
                value = value.TrimEnd();
                for (int index = 0; index < value.Length; index++)
                {
                    if (value[index] == '#' && (index == 0 || char.IsWhiteSpace(value[index - 1])))
                    {
                        value = value[..index].TrimEnd();
                        break;
                    }
                }
            }

            if (value.Contains('\0', StringComparison.Ordinal))
            {
                throw InvalidAssignment(path, declarationLine);
            }

            result[name] = value;
        }

        return result;
    }

    private static string ReadQuotedValue(string value, StringReader reader, string path, ref int lineNumber)
    {
        char quote = value[0];
        int declarationLine = lineNumber;
        var result = new StringBuilder();
        for (int index = 1; ; index++)
        {
            if (index == value.Length)
            {
                value = reader.ReadLine() ?? throw InvalidAssignment(path, declarationLine);
                lineNumber++;
                result.Append('\n');
                index = -1;
                continue;
            }

            char character = value[index];
            if (character == quote)
            {
                ReadOnlySpan<char> suffix = value.AsSpan(index + 1).TrimStart();
                if (suffix.Length != 0 && suffix[0] != '#')
                {
                    throw InvalidAssignment(path, lineNumber);
                }

                return result.ToString();
            }

            if (character == '\\' && index + 1 < value.Length)
            {
                char next = value[index + 1];
                if (next == quote || next == '\\')
                {
                    result.Append(next);
                    index++;
                    continue;
                }

                if (quote == '"' && next is 'n' or 'r' or 't')
                {
                    result.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : '\t');
                    index++;
                    continue;
                }
            }

            result.Append(character);
        }
    }

    private static InvalidDataException InvalidAssignment(string path, int lineNumber) =>
        new($"envFile '{path}' contains an invalid assignment at line {lineNumber}.");
}
