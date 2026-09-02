using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Csls.Mcp.Worker;

/// <summary>
/// Provides reusable agent prompts grounded in csls language-intelligence operations.
/// </summary>
[McpServerPromptType]
internal sealed class CslsMcpPrompts
{
    /// <summary>
    /// Creates the explicit AOT-safe prompt registration target.
    /// </summary>
    internal CslsMcpPrompts()
    {
    }

    /// <summary>
    /// Creates a diagnostics investigation prompt that requires root-cause analysis.
    /// </summary>
    /// <param name="scope">The workspace, project, or document scope to investigate.</param>
    /// <returns>The diagnostics investigation prompt.</returns>
    [McpServerPrompt(Name = "diagnose_csharp")]
    [Description("Investigate C# diagnostics with csls and require a verified root cause before proposing changes.")]
    public static string Diagnose(
        [Description("Workspace, project, or document scope to investigate.")]
        string scope) =>
        $"Inspect {scope} with csls. Group diagnostics by root cause, verify each cause against " +
        "language-server evidence, and propose the smallest complete correction.";

    /// <summary>
    /// Creates a symbol explanation prompt grounded in definitions, references, and hover data.
    /// </summary>
    /// <param name="symbol">The symbol to explain.</param>
    /// <returns>The symbol explanation prompt.</returns>
    [McpServerPrompt(Name = "explain_symbol")]
    [Description("Explain a C# symbol using csls hover, definition, reference, and project context.")]
    public static string Explain(
        [Description("C# symbol to explain.")]
        string symbol) =>
        $"Use csls to explain {symbol}, including its declaration, type, documentation, callers, " +
        "dependencies, and role in the explicitly selected workspace.";

    /// <summary>
    /// Creates a review prompt that uses semantic language-server evidence.
    /// </summary>
    /// <param name="scope">The code scope to review.</param>
    /// <returns>The semantic code-review prompt.</returns>
    [McpServerPrompt(Name = "review_csharp")]
    [Description("Review C# code using csls semantic evidence and actionable findings.")]
    public static string Review(
        [Description("Workspace, project, document, type, or member to review.")]
        string scope) =>
        $"Review {scope} with csls. Verify findings through symbols, references, diagnostics, and " +
        "language semantics; report only actionable correctness, security, or maintainability issues.";

    /// <summary>
    /// Creates a refactoring prompt with explicit edit preconditions and verification.
    /// </summary>
    /// <param name="goal">The requested refactoring outcome.</param>
    /// <returns>The guarded refactoring prompt.</returns>
    [McpServerPrompt(Name = "refactor_csharp")]
    [Description("Plan and apply a C# refactoring through csls with version preconditions and verification.")]
    public static string Refactor(
        [Description("Desired refactoring outcome.")]
        string goal) =>
        $"Use csls to implement this refactoring: {goal}. Inspect all affected symbols first, " +
        "require document-version or content-hash preconditions, apply explicit edits, and verify diagnostics.";

    /// <summary>
    /// Creates a language-server troubleshooting prompt grounded in session state and logs.
    /// </summary>
    /// <param name="symptom">The observed editor or language-server symptom.</param>
    /// <returns>The language-server troubleshooting prompt.</returns>
    [McpServerPrompt(Name = "troubleshoot_csls")]
    [Description("Troubleshoot a csls or editor integration symptom from session state, queues, logs, and workspace evidence.")]
    public static string Troubleshoot(
        [Description("Observed csls or editor integration symptom.")]
        string symptom) =>
        $"Troubleshoot this csls symptom: {symptom}. Inspect session lifecycle, workspace state, " +
        "request activity, logs, and editor-visible behavior; identify and verify the root cause.";
}
