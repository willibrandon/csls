namespace Csls.TestProcessHost;

/// <summary>
/// Provides an enumerable whose element type retains a genuine CLR rectangular array shape.
/// </summary>
internal sealed class ResultsViewRectangularFixture : ResultsViewFixture<int[,]>
{
    /// <summary>
    /// Creates one rectangular element with ordered values for debugger inspection.
    /// </summary>
    internal ResultsViewRectangularFixture() : base([(int[,])CreateItems()])
    {
    }

    private static Array CreateItems()
    {
        var items = Array.CreateInstance(typeof(int), 1, 2);
        items.SetValue(103, 0, 0);
        items.SetValue(104, 0, 1);
        return items;
    }
}
