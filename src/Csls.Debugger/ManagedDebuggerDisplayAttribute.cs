namespace Csls.Debugger;

/// <summary>
/// Describes one validated DebuggerDisplayAttribute decoded from target metadata.
/// </summary>
/// <param name="Value">The value-column display template.</param>
/// <param name="Name">The optional child-name display template.</param>
/// <param name="Type">The optional type-column display template.</param>
internal sealed record ManagedDebuggerDisplayAttribute(
    string Value,
    string? Name,
    string? Type);
