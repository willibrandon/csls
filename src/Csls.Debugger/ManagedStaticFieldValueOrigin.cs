namespace Csls.Debugger;

/// <summary>
/// Identifies static storage by exact closed declaring type, field token, and selected thread.
/// </summary>
/// <param name="DeclaringType">The immutable loaded type identity including generic arguments.</param>
/// <param name="FieldToken">The field-definition token within that type's module.</param>
/// <param name="ThreadId">The selected native thread owning thread-specific storage.</param>
internal sealed record ManagedStaticFieldValueOrigin(
    ManagedBoundType DeclaringType,
    uint FieldToken,
    int ThreadId) : ManagedValueOrigin;
