namespace Csls.TestProcessHost;

/// <summary>
/// Provides an enumerable element whose assembly identity can be duplicated across load contexts.
/// </summary>
internal sealed class ResultsViewElement
{
    /// <summary>
    /// Retains a value that can be read without evaluating target code.
    /// </summary>
    internal readonly int _value = 131;
}
