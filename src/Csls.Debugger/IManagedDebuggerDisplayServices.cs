using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Supplies generation-owned runtime operations to debugger display formatting.
/// </summary>
internal interface IManagedDebuggerDisplayServices
{
    /// <summary>
    /// Opens the current image for one loaded runtime module.
    /// </summary>
    /// <param name="module">The retained ICorDebugModule pointer.</param>
    /// <returns>A reader over the current managed module image.</returns>
    PEReader OpenRuntimeModule(nint module);

    /// <summary>
    /// Formats one nested runtime value at the supplied display recursion depth.
    /// </summary>
    /// <param name="value">The retained ICorDebugValue pointer.</param>
    /// <param name="debuggerDisplayDepth">The current debugger-display recursion depth.</param>
    /// <returns>The nested formatted value.</returns>
    ManagedValueDisplay FormatRuntimeValue(nint value, int debuggerDisplayDepth);
}
