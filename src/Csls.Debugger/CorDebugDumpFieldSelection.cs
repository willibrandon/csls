namespace Csls.Debugger;

/// <summary>
/// Identifies a physical instance field independently of a virtual process's native interface cache.
/// </summary>
/// <param name="ModuleAddress">The captured declaring module's base address.</param>
/// <param name="DeclaringTypeToken">The declaring type definition token scoped to that module.</param>
/// <param name="FieldToken">The instance field definition token scoped to that module.</param>
internal readonly record struct CorDebugDumpFieldSelection(ulong ModuleAddress, uint DeclaringTypeToken, uint FieldToken);
