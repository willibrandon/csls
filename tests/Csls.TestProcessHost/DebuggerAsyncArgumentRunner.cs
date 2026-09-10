using System.Reflection;
using System.Runtime.Loader;

namespace Csls.TestProcessHost;

/// <summary>
/// Runs compiler-built language fixtures through the process host's asynchronous entry point.
/// </summary>
internal static class DebuggerAsyncArgumentRunner
{
    /// <summary>
    /// Loads and awaits a closed generic language fixture with real captured source parameters.
    /// </summary>
    /// <param name="assemblyPath">The absolute compiled language-fixture path.</param>
    /// <param name="argument">The original source argument.</param>
    /// <param name="replacement">The value the debugger assigns before consumption.</param>
    /// <param name="receiverValue">The value retained by the original receiver.</param>
    /// <returns>The fixture's exit code after resumption.</returns>
    internal static Task<int> RunAsync(string assemblyPath, string argument, string replacement, string receiverValue)
    {
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Type type = assembly.GetTypes().Single(type => type.Name == "DebuggerGenericFixture`1")
            .MakeGenericType(typeof(string));
        object instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [receiverValue], culture: null)
            ?? throw new InvalidOperationException("The language fixture constructor returned no instance.");
        MethodInfo method = type.GetMethod("RunCapturedAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The language fixture has no captured-argument method.");
        return method.Invoke(instance, [argument, replacement, 17]) as Task<int>
            ?? throw new InvalidOperationException("The language fixture returned no asynchronous result.");
    }

    /// <summary>
    /// Invokes a compiled async method with closed generic and constructed parameter types.
    /// </summary>
    /// <param name="assemblyPath">The absolute compiled C# fixture path.</param>
    /// <returns>The fixture result after the debugger inspects its declared argument types.</returns>
    internal static Task<int> RunUnusedShapesAsync(string assemblyPath)
    {
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Type type = assembly.GetTypes().Single(type => type.Name == "DebuggerGenericFixture`1")
            .MakeGenericType(typeof(int));
        MethodInfo method = (type.GetMethod("RunUnusedShapesAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The fixture has no declared-type inspection method."))
            .MakeGenericMethod(typeof(string), typeof(string).MakeArrayType(2), typeof(int).MakeArrayType(1));
        int[] lengths = [2];
        int[] lowerBounds = [3];
        return method.Invoke(null,
            [23, "method-value", Array.Empty<int>(), Array.CreateInstance(typeof(string), 1, 1), 17, (19, "tuple"),
                new Dictionary<string, List<int?[]>>(), Array.Empty<int[]>(), 31m,
                Array.CreateInstance(typeof(int), lengths, lowerBounds)]) as Task<int>
            ?? throw new InvalidOperationException("The declared-type fixture returned no asynchronous result.");
    }
}
