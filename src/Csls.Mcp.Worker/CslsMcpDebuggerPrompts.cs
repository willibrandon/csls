using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Provides read-first prompts for explicit debugger-session workflows.
/// </summary>
[McpServerPromptType]
internal sealed class CslsMcpDebuggerPrompts
{
    /// <summary>
    /// Creates the explicit prompt registration target.
    /// </summary>
    internal CslsMcpDebuggerPrompts()
    {
    }

    /// <summary>
    /// Creates a debugger failure-investigation prompt.
    /// </summary>
    [McpServerPrompt(Name = "diagnose_dotnet_debugger_failure")]
    [Description("Diagnose a .NET debugger failure from explicit session state and bounded evidence.")]
    public static string DiagnoseFailure(
        [Description("Opaque debugger-session identifier.")]
        string debugSession,
        [Description("Observed failure or unexpected behavior.")]
        string symptom) =>
        $"Diagnose this debugger symptom for debugSession {debugSession}: {symptom}. " +
        "Read session state, output, modules, threads, and the current stopped stack as applicable. " +
        "Identify a verified root cause. Do not resume, step, move execution, or change breakpoints.";

    /// <summary>
    /// Creates a breakpoint-planning prompt without applying mutations.
    /// </summary>
    [McpServerPrompt(Name = "plan_dotnet_breakpoints")]
    [Description("Plan .NET breakpoints from source and debugger evidence without changing the target.")]
    public static string PlanBreakpoints(
        [Description("Opaque debugger-session identifier.")]
        string debugSession,
        [Description("Behavior or failure the breakpoints should isolate.")]
        string goal) =>
        $"Plan breakpoints for debugSession {debugSession} to isolate: {goal}. Inspect current " +
        "modules, stacks, source, and language symbols. Return a minimal ordered breakpoint plan " +
        "with rationale. Do not set breakpoints or change target execution.";

    /// <summary>
    /// Creates a stopped-state explanation prompt.
    /// </summary>
    [McpServerPrompt(Name = "explain_dotnet_debugger_state")]
    [Description("Explain one explicit .NET debugger session from generation-consistent evidence.")]
    public static string ExplainState(
        [Description("Opaque debugger-session identifier.")]
        string debugSession) =>
        $"Explain the current state of debugSession {debugSession}. Read its exact current " +
        "stopGeneration, stopped thread, stack, scopes, variables, exception when applicable, " +
        "modules, and recent output. Clearly separate observed facts from inference. Do not mutate.";

    /// <summary>
    /// Creates a managed process-dump triage prompt.
    /// </summary>
    [McpServerPrompt(Name = "triage_dotnet_dump")]
    [Description("Triage one explicit read-only .NET process-dump session from bounded evidence.")]
    public static string TriageDump(
        [Description("Opaque debugger-session identifier returned by debug_dump_open.")]
        string debugSession,
        [Description("Failure, symptom, or question the dump should explain.")]
        string question) =>
        $"Triage the managed process dump in debugSession {debugSession} to answer: {question}. " +
        "First read debug_session_get and verify that mode is dump. Preserve its exact " +
        "stopGeneration while reading debug_threads_get, bounded debug_stack_get pages for " +
        "relevant threads, and bounded debug_modules_get pages. Correlate repeated and blocked " +
        "stacks, runtime infrastructure, application modules, and missing evidence. Clearly " +
        "separate observed facts from inference, rank likely causes, and identify the next " +
        "artifact needed when the dump cannot prove the cause. This session is read-only: do not " +
        "grant control, open another dump, execute expressions, resume, step, set breakpoints, or " +
        "claim that unavailable locals, exceptions, memory, source, or disassembly were inspected.";
}
