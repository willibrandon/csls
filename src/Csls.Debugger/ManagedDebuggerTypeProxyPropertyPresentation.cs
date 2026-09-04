using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Describes one final-generation proxy property row and its browsing policy.
/// </summary>
/// <param name="Variable">The debugger-facing property variable.</param>
/// <param name="BrowsingState">The declared debugger browsing policy.</param>
internal sealed record ManagedDebuggerTypeProxyPropertyPresentation(
    DebugVariableInfo Variable,
    ManagedDebuggerBrowsableState BrowsingState);
