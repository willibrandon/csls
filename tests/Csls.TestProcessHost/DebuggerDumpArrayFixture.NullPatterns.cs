namespace Csls.TestProcessHost;

/// <summary>
/// Retains distinct CLR array layouts while an independent process captures its heap.
/// </summary>
internal static partial class DebuggerDumpArrayFixture
{
    /// <summary>
    /// Applies the target compiler's null-pattern semantics to an arbitrary boxed value.
    /// </summary>
    /// <param name="value">The value tested by compiler-authored target code.</param>
    /// <returns>True when the compiler treats the value as null.</returns>
    internal static bool CompilerIsNull(object? value) => value is null;
}
