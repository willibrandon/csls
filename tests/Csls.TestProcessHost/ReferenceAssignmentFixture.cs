using System.Runtime.CompilerServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Keeps an ordinary string location and its managed by-reference alias available to debugger inspection.
/// </summary>
internal static class ReferenceAssignmentFixture
{
    /// <summary>
    /// Retains both reference shapes across a managed call without changing existing debugger fixture locals.
    /// </summary>
    /// <param name="path">The signal file consumed by the nested debugger fixture.</param>
    /// <returns>The nested fixture exit code.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string path)
    {
        string? target = "reference-assignment-value";
        ref string? alias = ref target;
        int result = DebuggerFixture.WaitForSignal(
            path, "ready", 42, "answer", (ArgumentNumber: 42, ArgumentText: "argument"));
        GC.KeepAlive(target);
        GC.KeepAlive(alias);
        return result;
    }
}
