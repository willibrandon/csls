namespace Csls.Debugger;

/// <summary>
/// Carries a language-neutral debugger value and type display.
/// </summary>
/// <param name="Value">The formatted immediate value.</param>
/// <param name="Type">The runtime element-type display.</param>
internal readonly record struct ManagedValueDisplay(string Value, string Type);
