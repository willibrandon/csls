namespace Csls.TestProcessHost;

/// <summary>
/// Retains a generic enumerable interface through an inherited metadata signature.
/// </summary>
/// <typeparam name="T">The exact runtime element type.</typeparam>
internal interface IResultsViewEnumerable<T> : IEnumerable<T>
{
}
