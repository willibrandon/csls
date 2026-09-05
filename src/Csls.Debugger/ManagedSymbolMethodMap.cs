namespace Csls.Debugger;

/// <summary>
/// Reads visible source sequence points for one managed method definition.
/// </summary>
internal static class ManagedSymbolMethodMap
{
    /// <summary>
    /// Reads the ordered visible sequence points for a method.
    /// </summary>
    /// <param name="frame">The generation-bound frame and immutable symbol snapshot.</param>
    /// <param name="methodToken">The method-definition metadata token.</param>
    /// <returns>The ordered visible source positions.</returns>
    internal static IReadOnlyList<ManagedSequencePoint> Read(
        ManagedFrameHandle frame,
        uint methodToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using DebugSymbolReader? symbols = frame.OpenSymbols();
        if (symbols is null)
        {
            return [];
        }

        return symbols.GetSequencePoints(methodToken);
    }
}
