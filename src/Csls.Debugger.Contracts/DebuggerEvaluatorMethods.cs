namespace Csls.Debugger.Contracts;

/// <summary>
/// Names the versioned private managed evaluator methods.
/// </summary>
public static class DebuggerEvaluatorMethods
{
    /// <summary>
    /// Gets the managed evaluator protocol version.
    /// </summary>
    public const string GetProtocolVersion = "debuggerEvaluator/getProtocolVersion";

    /// <summary>
    /// Compiles a source expression into language-neutral operations.
    /// </summary>
    public const string Compile = "debuggerEvaluator/compile";
}
