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
        ApplyBoundedAllocationPressure(loadContext);

        WriteSignalAtomically(
            unloadedSignalPath,
            loadContext.IsAlive ? "retained" : "unloaded");
        WaitForFile(finishSignalPath);
        return fixtureExitCode != 0 ? fixtureExitCode : loadContext.IsAlive ? 4 : 0;
    }

    private static void ApplyBoundedAllocationPressure(WeakReference loadContext)
    {
        const int allocationSize = 1024 * 1024;
        byte[][] retainedPressure = new byte[32][];
        for (int attempt = 0; attempt < 2048 && loadContext.IsAlive; attempt++)
        {
            retainedPressure[attempt % retainedPressure.Length] =
                GC.AllocateUninitializedArray<byte>(allocationSize);
            if (attempt % retainedPressure.Length == retainedPressure.Length - 1)
            {
                GC.WaitForPendingFinalizers();
                Thread.Sleep(1);
            }
        }

        GC.KeepAlive(retainedPressure);
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
            Thread.Sleep(1);
        }
    }

    private static void WriteSignalAtomically(string path, string content)
    {
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path);
    }
}
