namespace Csls.Debugger;

/// <summary>
/// Carries decoded debugger-display data including assembly target metadata.
/// </summary>
/// <param name="Value">The value-column display template.</param>
/// <param name="Name">The optional child-name display template.</param>
/// <param name="Type">The optional type-column display template.</param>
/// <param name="TargetTypeName">The optional assembly-level target type name.</param>
internal sealed record ManagedDebuggerDisplayMetadata(
    string Value,
    string? Name,
    string? Type,
    string? TargetTypeName);
