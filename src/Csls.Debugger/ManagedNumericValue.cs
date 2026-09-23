namespace Csls.Debugger;

/// <summary>
/// Carries one built-in numeric value normalized for language-neutral operations.
/// </summary>
/// <param name="Kind">The normalized arithmetic domain.</param>
/// <param name="Value">The value represented in that domain.</param>
internal readonly record struct ManagedNumericValue(
    ManagedNumericKind Kind,
    object Value);
