namespace Csls.TestProcessHost;

/// <summary>
/// Exposes explicit result overloads ranked through lifted nullable target identities.
/// </summary>
internal readonly struct DebuggerExplicitNullableTargetRankingSource
{
    private readonly byte _number;

    /// <summary>
    /// Initializes an explicit nullable target-ranking source.
    /// </summary>
    /// <param name="number">The source number.</param>
    internal DebuggerExplicitNullableTargetRankingSource(byte number) => _number = number;

    /// <summary>
    /// Converts through the narrower nullable result candidate.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The byte result.</returns>
    public static explicit operator byte(DebuggerExplicitNullableTargetRankingSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return source._number;
    }

    /// <summary>
    /// Converts through the nearer nullable result candidate.
    /// </summary>
    /// <param name="source">The authored source value.</param>
    /// <returns>The short result marker.</returns>
    public static explicit operator short(DebuggerExplicitNullableTargetRankingSource source)
    {
        DebuggerImplicitConversionFixture.RecordConversion();
        return (short)(source._number + 1000);
    }
}
