namespace Csls.Debugger;

/// <summary>
/// Carries a language-neutral debugger value and type display.
/// </summary>
/// <param name="Value">The formatted immediate value.</param>
/// <param name="Type">The runtime element-type display.</param>
/// <param name="Name">The optional debugger-provided name for a child row.</param>
internal readonly record struct ManagedValueDisplay(
    string Value,
    string Type,
    string? Name = null);
