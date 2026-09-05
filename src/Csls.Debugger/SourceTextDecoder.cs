using System.Text;

namespace Csls.Debugger;

/// <summary>
/// Decodes exact source bytes while honoring Unicode byte-order marks.
/// </summary>
internal static class SourceTextDecoder
{
    /// <summary>
    /// Decodes source bytes as strict UTF-8 or their byte-order-mark encoding.
    /// </summary>
    /// <param name="source">The exact source bytes.</param>
    /// <returns>The decoded source text.</returns>
    internal static string Decode(byte[] source)
    {
        using var stream = new MemoryStream(source, writable: false);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
