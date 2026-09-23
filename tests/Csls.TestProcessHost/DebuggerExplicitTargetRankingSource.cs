namespace Csls.TestProcessHost;

/// <summary>
/// Exposes explicit result overloads that require most-encompassed target selection.
/// </summary>
internal readonly struct DebuggerExplicitTargetRankingSource
{
    private readonly int _number;

    /// <summary>
    /// Initializes an explicit target-ranking source.
    /// </summary>
    /// <param name="number">The source number.</param>
    internal DebuggerExplicitTargetRankingSource(int number) => _number = number;

    /// <summary>
    /// Converts through the nearer result type.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The integer result marker.</returns>
    public static explicit operator int(DebuggerExplicitTargetRankingSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source._number + 1000;
    }

    /// <summary>
    /// Converts through the checked partner for the nearer result type.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The checked integer result marker.</returns>
    public static explicit operator checked int(DebuggerExplicitTargetRankingSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source._number + 3000;
    }

    /// <summary>
    /// Converts through the wider result type.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The long result marker.</returns>
    public static explicit operator long(DebuggerExplicitTargetRankingSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source._number + 2000L;
    }
}
