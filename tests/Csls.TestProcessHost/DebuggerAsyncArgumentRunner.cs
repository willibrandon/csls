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
}
