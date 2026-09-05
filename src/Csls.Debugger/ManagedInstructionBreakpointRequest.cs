namespace Csls.Debugger;

/// <summary>
/// Carries one resolved managed-IL breakpoint request into runtime binding.
/// </summary>
internal sealed class ManagedInstructionBreakpointRequest
{
    /// <summary>
    /// Gets or initializes the original instruction reference.
    /// </summary>
    internal required string InstructionReference { get; init; }

    /// <summary>
    /// Gets or initializes the requested signed byte offset.
    /// </summary>
    internal required long Offset { get; init; }

    /// <summary>
    /// Gets or initializes the optional source-language Boolean condition.
    /// </summary>
    internal string? Condition { get; init; }

    /// <summary>
    /// Gets or initializes the optional hit-count expression.
    /// </summary>
    internal string? HitCondition { get; init; }

    /// <summary>
    /// Gets or initializes the normalized module path when resolution succeeded.
    /// </summary>
    internal string? ModulePath { get; init; }

    /// <summary>
    /// Gets or initializes the stable session-local module identifier.
    /// </summary>
    internal int? ModuleId { get; init; }

    /// <summary>
    /// Gets or initializes the managed method token when resolution succeeded.
    /// </summary>
    internal uint MethodToken { get; init; }

    /// <summary>
    /// Gets or initializes the exact managed-IL offset when resolution succeeded.
    /// </summary>
    internal uint IlOffset { get; init; }

    /// <summary>
    /// Gets or initializes an address-validation failure.
    /// </summary>
    internal string? ValidationMessage { get; init; }
}
