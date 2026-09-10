namespace Csls.TestProcessHost;

/// <summary>
/// Closes an inherited enumerable over an array element type.
/// </summary>
internal sealed class ResultsViewInheritedFixture : ResultsViewFixture<int[]>
{
    /// <summary>
    /// Creates a collection with nested values that prove inherited type substitution.
    /// </summary>
    internal ResultsViewInheritedFixture() : base([[101], [102]])
    {
    }
}
