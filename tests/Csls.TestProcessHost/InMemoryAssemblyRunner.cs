using System.Reflection;

namespace Csls.TestProcessHost;

/// <summary>
/// Loads a real managed PE and Portable PDB from byte arrays for debugger coverage.
/// </summary>
internal static class InMemoryAssemblyRunner
{
    /// <summary>
    /// Loads and invokes the in-memory debugger fixture through its public entry point.
    /// </summary>
    /// <param name="assemblyPath">The source PE image path.</param>
    /// <param name="symbolPath">The source Portable PDB image path.</param>
    /// <param name="signalPath">The file whose creation releases the fixture.</param>
    /// <param name="announce">Whether to announce that the assembly is loaded before invocation.</param>
    /// <returns>The fixture's integer result.</returns>
    internal static int Run(
        string assemblyPath,
        string symbolPath,
        string signalPath,
        bool announce)
    {
        byte[] assemblyImage = File.ReadAllBytes(assemblyPath);
        byte[] symbolImage = File.ReadAllBytes(symbolPath);
        var assembly = Assembly.Load(assemblyImage, symbolImage);
        Type fixture = assembly.GetType(
            "Csls.Debugger.Fixtures.InMemory.InMemoryFixture",
            throwOnError: true,
            ignoreCase: false)!;
        MethodInfo method = fixture.GetMethod(
            "WaitForSignal",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(fixture.FullName, "WaitForSignal");
        if (announce)
        {
            Console.Out.Write("ready");
            Console.Out.Flush();
        }

        return (int)(method.Invoke(obj: null, [signalPath])
            ?? throw new InvalidOperationException("The in-memory fixture returned no result."));
    }
}
