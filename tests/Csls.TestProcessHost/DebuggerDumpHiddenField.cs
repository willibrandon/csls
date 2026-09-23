namespace Csls.TestProcessHost;

/// <summary>
/// Retains distinct private fields with the same name in constructed derived and base declarations.
/// </summary>
internal sealed class DebuggerDumpHiddenField : DebuggerDumpFieldBase<int[]>
{
    private readonly int[] _value = [201];

    /// <summary>
    /// Initializes different values for the two physical field declarations.
    /// </summary>
    internal DebuggerDumpHiddenField() : base([101]) => GC.KeepAlive(GetBaseValue());

    /// <summary>
    /// Returns the derived field through ordinary target code.
    /// </summary>
    /// <returns>The retained derived value.</returns>
    internal int[] GetDerivedValue() => _value;
}
