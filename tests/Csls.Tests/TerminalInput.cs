using Hex1b;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Sends canonical terminal input sequences through a Hex1b terminal.
/// </summary>
internal static class TerminalInput
{
    /// <summary>
    /// Sends an Alt-modified ASCII character as its canonical escape-prefixed sequence.
    /// </summary>
    internal static Task SendAltCharacterAsync(
        Hex1bTerminal terminal,
        char character,
        CancellationToken cancellationToken)
    {
        if (!char.IsAscii(character))
        {
            throw new ArgumentOutOfRangeException(
                nameof(character),
                character,
                "Alt-modified terminal input requires an ASCII character.");
        }

        byte[] input = [0x1b, Encoding.ASCII.GetBytes([character])[0]];
        return terminal.SendInputAsync(input, cancellationToken);
    }
}
