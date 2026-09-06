using System.Runtime.CompilerServices;

namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Requests explicit debugger stops inside a source-visible call while preserving a live argument.
/// </summary>
internal static class ManagedBreakFixture
{
    /// <summary>
    /// Calls the break-request method from a stable source breakpoint and verifies its result.
    /// </summary>
    /// <param name="number">The input kept live across explicit break requests.</param>
    /// <returns>Zero when both break requests preserve the expected computation.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(int number)
    {
        int result = ReadNumber(number);
        return result - 43;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadNumber(int number)
    {
        System.Diagnostics.Debugger.Break();
        number++;
        System.Diagnostics.Debugger.Break();
        return number;
    }
}
