namespace Csls.Debugger;

/// <summary>
/// Identifies a half-open IL range for one source statement.
/// </summary>
/// <param name="StartOffset">The inclusive IL start offset.</param>
/// <param name="EndOffset">The exclusive IL end offset.</param>
internal readonly record struct ManagedStepRange(uint StartOffset, uint EndOffset);
