using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Represents a half-open range between two UTF-16 document positions.
/// </summary>
public readonly record struct Range
{
    /// <summary>
    /// Initializes a validated document range.
    /// </summary>
    /// <param name="start">The inclusive start position.</param>
    /// <param name="end">The exclusive end position.</param>
    [JsonConstructor]
    public Range(Position start, Position end)
    {
        if (end.Line < start.Line ||
            end.Line == start.Line && end.Character < start.Character)
        {
            throw new ArgumentException("The range end cannot precede its start.", nameof(end));
        }

        Start = start;
        End = end;
    }

    /// <summary>
    /// Gets the inclusive start position.
    /// </summary>
    public Position Start { get; }

    /// <summary>
    /// Gets the exclusive end position.
    /// </summary>
    public Position End { get; }
}
