using System.Globalization;

namespace Csls.Debugger;

/// <summary>
/// Tracks one validated breakpoint hit-count predicate.
/// </summary>
internal sealed class DebugHitCondition
{
    private uint _hitCount;

    private DebugHitCondition(DebugHitConditionKind kind, uint value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>
    /// Gets the hit-count comparison kind.
    /// </summary>
    internal DebugHitConditionKind Kind { get; }

    /// <summary>
    /// Gets the positive comparison value.
    /// </summary>
    internal uint Value { get; }

    /// <summary>
    /// Gets the diagnostic returned for an invalid hit condition.
    /// </summary>
    internal const string ValidationErrorMessage =
        "Unable to parse hitCondition. Expected a positive number, >=number, or %number.";

    /// <summary>
    /// Parses the established integer, greater-or-equal, and modulo forms.
    /// </summary>
    /// <param name="text">The optional DAP hit condition.</param>
    /// <param name="condition">The parsed condition, or null when absent or invalid.</param>
    /// <returns>True when the value is absent or valid.</returns>
    internal static bool TryParse(string? text, out DebugHitCondition? condition)
    {
        condition = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string value = text.Trim();
        DebugHitConditionKind kind = DebugHitConditionKind.Equal;
        if (value.StartsWith(">=", StringComparison.Ordinal))
        {
            kind = DebugHitConditionKind.GreaterThanOrEqual;
            value = value[2..].TrimStart();
        }
        else if (value.StartsWith('%'))
        {
            kind = DebugHitConditionKind.Multiple;
            value = value[1..].TrimStart();
        }

        if (!uint.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out uint count) || count == 0)
        {
            return false;
        }

        condition = new DebugHitCondition(kind, count);
        return true;
    }

    /// <summary>
    /// Records one logical breakpoint hit and evaluates the predicate.
    /// </summary>
    /// <returns>True when this hit should stop the target.</returns>
    internal bool RegisterHit()
    {
        if (_hitCount < uint.MaxValue)
        {
            _hitCount++;
        }

        return Kind switch
        {
            DebugHitConditionKind.Equal => _hitCount == Value,
            DebugHitConditionKind.GreaterThanOrEqual => _hitCount >= Value,
            DebugHitConditionKind.Multiple => _hitCount % Value == 0,
            _ => false
        };
    }
}
