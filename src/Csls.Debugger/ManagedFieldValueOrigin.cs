namespace Csls.Debugger;

/// <summary>
/// Identifies an exact instance field relative to its physical containing storage.
/// </summary>
/// <param name="Parent">The containing value's physical origin.</param>
/// <param name="ModuleId">The exact loaded module declaring the field.</param>
/// <param name="TypeToken">The declaring type-definition metadata token.</param>
/// <param name="FieldToken">The field-definition metadata token.</param>
internal sealed record ManagedFieldValueOrigin(
    ManagedValueOrigin Parent,
    int ModuleId,
    uint TypeToken,
    uint FieldToken) : ManagedValueOrigin;
