namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one debugger variable scope at a stopped frame.
/// </summary>
/// <param name="Name">The scope display name.</param>
/// <param name="VariablesReference">The generation-bound variable-container handle.</param>
/// <param name="Expensive">Whether expansion is expected to require substantial work.</param>
public sealed record DebugScopeInfo(string Name, int VariablesReference, bool Expensive);
