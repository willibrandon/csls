namespace Csls.Debugger.Terminal;

/// <summary>
/// Captures one terminal-owned session for a versioned browser selection.
/// </summary>
/// <param name="Id">The exact terminal-local session identity.</param>
/// <param name="Label">The browser row shown to the developer.</param>
internal sealed record DebuggerTerminalSessionOption(Guid Id, string Label);
