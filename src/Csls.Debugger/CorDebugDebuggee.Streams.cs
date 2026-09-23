using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Copies bounded managed target output away from protocol streams.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    /// <inheritdoc />
    public Task CopyStandardOutputAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken) =>
        CopyAsync(_standardOutput, writeAsync, cancellationToken);

    /// <inheritdoc />
    public Task CopyStandardErrorAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken) =>
        CopyAsync(_standardError, writeAsync, cancellationToken);

    private static StreamReader CreateReader(Stream stream) =>
        new(
            stream,
            OperatingSystem.IsWindows() ? Console.OutputEncoding : Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

    private static async Task CopyAsync(
        TextReader reader,
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            await writeAsync(new string(buffer, 0, count), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
