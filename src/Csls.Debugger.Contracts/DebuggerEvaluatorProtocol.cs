namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines the private managed evaluator protocol version.
/// </summary>
public static class DebuggerEvaluatorProtocol
{
    /// <summary>
    /// Gets the exact expression-plan version understood by the runtime binder.
    /// </summary>
    public const int CurrentPlanVersion = 2;

    /// <summary>
    /// Gets the exact evaluator protocol version implemented by this build.
    /// </summary>
    public const int CurrentVersion = 2;
}
