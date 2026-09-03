using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Csls.TestProcessHost;

/// <summary>
/// Loads, invokes, and unloads a managed debugger fixture in a collectible context.
/// </summary>
internal static class CollectibleAssemblyRunner
{
    /// <summary>
    /// Coordinates a collectible module lifetime through observable file boundaries.
    /// </summary>
    /// <param name="assemblyPath">The absolute managed fixture assembly path.</param>
    /// <param name="loadSignalPath">The file whose creation starts module loading.</param>
    /// <param name="fixtureSignalPath">The file whose creation releases the loaded fixture.</param>
    /// <param name="unloadedSignalPath">The file written after collectible-context reclamation.</param>
    /// <param name="finishSignalPath">The file whose creation permits process exit.</param>
    /// <returns>Zero when the fixture succeeds and its load context is reclaimed.</returns>
    internal static int Run(
        string assemblyPath,
        string loadSignalPath,
        string fixtureSignalPath,
        string unloadedSignalPath,
        string finishSignalPath)
    {
        Console.Out.Write("ready");
        Console.Out.Flush();
        WaitForFile(loadSignalPath);
        (int fixtureExitCode, WeakReference loadContext) = InvokeCollectibleFixture(
            assemblyPath,
            fixtureSignalPath);
        for (int attempt = 0; attempt < 100 && loadContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }

        File.WriteAllText(
            unloadedSignalPath,
            loadContext.IsAlive ? "retained" : "unloaded");
        WaitForFile(finishSignalPath);
        return fixtureExitCode != 0 ? fixtureExitCode : loadContext.IsAlive ? 4 : 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int ExitCode, WeakReference LoadContext) InvokeCollectibleFixture(
        string assemblyPath,
        string fixtureSignalPath)
    {
        var context = new AssemblyLoadContext("csls-module-churn", isCollectible: true);
        var reference = new WeakReference(context, trackResurrection: false);
        Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        MethodInfo entryPoint = assembly.EntryPoint
            ?? throw new MissingMethodException(assembly.FullName, "<entry point>");
        object? result = entryPoint.Invoke(
            obj: null,
            [new[] { fixtureSignalPath, "41", "module-churn" }]);
        context.Unload();
        return (
            result as int? ?? throw new InvalidOperationException(
                "The collectible debugger fixture returned no integer exit code."),
            reference);
    }

    private static void WaitForFile(string path)
    {
        while (!File.Exists(path))
        {
            Thread.SpinWait(10_000);
        }
    }
}
