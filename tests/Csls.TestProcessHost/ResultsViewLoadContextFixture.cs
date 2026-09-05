using System.Reflection;
using System.Runtime.Loader;

namespace Csls.TestProcessHost;

/// <summary>
/// Creates identically named enumerable elements with distinct runtime assembly identities.
/// </summary>
internal static class ResultsViewLoadContextFixture
{
    /// <summary>
    /// Creates an enumerable within the assembly load context executing this method.
    /// </summary>
    /// <returns>A collection containing an element from this exact assembly instance.</returns>
    public static object CreateEnumerable() => new ResultsViewFixture<ResultsViewElement>([new()]);

    /// <summary>
    /// Loads a second copy of the fixture assembly and creates an enumerable in that copy.
    /// </summary>
    /// <returns>An enumerable whose element type belongs to the isolated assembly instance.</returns>
    internal static object CreateIsolatedEnumerable()
    {
        var context = new AssemblyLoadContext("ResultsViewFixture");
        Assembly assembly = context.LoadFromAssemblyPath(typeof(ResultsViewLoadContextFixture).Assembly.Location);
        Type factory = assembly.GetType(typeof(ResultsViewLoadContextFixture).FullName!, throwOnError: true)!;
        MethodInfo create = factory.GetMethod(nameof(CreateEnumerable), BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(factory.FullName, nameof(CreateEnumerable));
        return create.Invoke(null, null)
            ?? throw new InvalidOperationException("The isolated Results View fixture returned null.");
    }
}
