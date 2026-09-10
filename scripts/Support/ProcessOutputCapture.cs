using System.Text;

namespace Csls.Support;

/// <summary>
/// Captures command output while forwarding available progress before the command exits.
/// </summary>
internal static class ProcessOutputCapture
{
    /// <summary>
    /// Drains a command stream without waiting for a newline and optionally mirrors its text.
    /// </summary>
    internal static async Task<string> ReadAsync(
        Stream stream,
        Encoding encoding,
        TextWriter? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(encoding);
        var output = new StringBuilder();
        byte[] bytes = new byte[4096];
        char[] characters = new char[encoding.GetMaxCharCount(bytes.Length)];
        Decoder decoder = encoding.GetDecoder();
        Exception? progressFailure = null;
        while (true)
        {
            int byteCount = await stream.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            int count = decoder.GetChars(bytes.AsSpan(0, byteCount), characters, flush: byteCount == 0);
            output.Append(characters, 0, count);
            if (count != 0 && progress is not null && progressFailure is null)
            {
                try
                {
                    await progress.WriteAsync(characters.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    await progress.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    // A broken log destination must not leave the child blocked on its pipe.
                    // Continue draining, then report the original forwarding failure.
                    progressFailure = exception;
                }
            }

            if (byteCount == 0)
            {
                break;
            }
        }

        if (progressFailure is not null)
        {
            throw new IOException("Forwarding command progress failed.", progressFailure);
        }

        return output.ToString();
    }
}
