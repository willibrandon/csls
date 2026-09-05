using System.Collections;
using System.Runtime.Loader;

namespace Csls.TestProcessHost;

/// <summary>
/// Throws a file-backed hostile exception whose type name imitates the LINQ empty sentinel.
/// </summary>
internal sealed class ResultsViewSpoofedExceptionFixture : IEnumerable<int>
{
    private readonly Exception _exception;

    /// <summary>
    /// Records whether the hostile exception was reached through genuine target enumeration.
    /// </summary>
    internal int _enumerationCount;

    /// <summary>
    /// Loads the authored hostile exception from its actual test assembly.
    /// </summary>
    /// <param name="assemblyPath">The absolute path of the test-authored exception assembly.</param>
    internal ResultsViewSpoofedExceptionFixture(string assemblyPath)
    {
        var context = new AssemblyLoadContext("ResultsViewSentinelIdentity");
        Type exceptionType = context.LoadFromAssemblyPath(assemblyPath)
            .GetType("System.Linq.SystemCore_EnumerableDebugViewEmptyException", throwOnError: true)!;
        _exception = (Exception)(Activator.CreateInstance(exceptionType)
            ?? throw new InvalidOperationException("The hostile exception could not be created."));
    }

    /// <summary>
    /// Throws the authored exception after recording one attempt to enumerate.
    /// </summary>
    /// <returns>This enumerator never returns successfully.</returns>
    public IEnumerator<int> GetEnumerator()
    {
        _enumerationCount++;
        throw _exception;
    }

    /// <summary>
    /// Routes non-generic enumeration through the same hostile exception path.
    /// </summary>
    /// <returns>This enumerator never returns successfully.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
