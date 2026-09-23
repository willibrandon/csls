namespace Csls.Debugger;

/// <summary>
/// Identifies an original heap owner retained in the current stopped generation.
/// </summary>
/// <param name="ValueReference">The retained original owner, valid only before its generation retires.</param>
internal sealed record ManagedHeapValueOrigin(int ValueReference) : ManagedValueOrigin;
