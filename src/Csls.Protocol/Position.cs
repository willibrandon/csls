namespace Csls.Protocol;

/// <summary>
/// Identifies a zero-based UTF-16 line and character position in a text document.
/// </summary>
public readonly record struct Position
{
    /// <summary>
    /// Initializes a validated position.
    /// </summary>
    /// <param name="line">The zero-based line.</param>
    /// <param name="character">The zero-based UTF-16 code-unit offset.</param>
    public Position(int line, int character)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfNegative(character);
        Line = line;
        Character = character;
    }

    /// <summary>
    /// Gets the zero-based line.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// Gets the zero-based UTF-16 code-unit offset.
    /// </summary>
    public int Character { get; }
}
