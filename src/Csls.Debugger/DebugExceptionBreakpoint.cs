using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Stores one validated managed exception-stage and type policy.
/// </summary>
internal sealed class DebugExceptionBreakpoint
{
    private const int MaximumTypeNameLength = 1024;
    private readonly HashSet<string> _exceptionTypeNames;

    private DebugExceptionBreakpoint(
        DebugExceptionBreakMode breakMode,
        HashSet<string> exceptionTypeNames)
    {
        BreakMode = breakMode;
        _exceptionTypeNames = exceptionTypeNames;
    }

    /// <summary>
    /// Gets the managed exception stage matched by this policy.
    /// </summary>
    internal DebugExceptionBreakMode BreakMode { get; }

    /// <summary>
    /// Creates one validated immutable engine policy.
    /// </summary>
    /// <param name="request">The transport-safe policy request.</param>
    /// <returns>The validated engine policy.</returns>
    internal static DebugExceptionBreakpoint Create(DebugExceptionBreakpointRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.BreakMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Unknown managed exception break mode {request.BreakMode}.");
        }

        ArgumentNullException.ThrowIfNull(request.ExceptionTypeNames);
        var names = request.ExceptionTypeNames
            .Select(NormalizeTypeName)
            .ToHashSet(StringComparer.Ordinal);

        return new DebugExceptionBreakpoint(request.BreakMode, names);
    }

    /// <summary>
    /// Determines whether a managed exception stage and hierarchy match this policy.
    /// </summary>
    /// <param name="breakMode">The current managed exception stage.</param>
    /// <param name="typeHierarchy">The exact type followed by its base types.</param>
    /// <returns>True when this policy requests a debugger stop.</returns>
    internal bool Matches(
        DebugExceptionBreakMode breakMode,
        IReadOnlyList<string> typeHierarchy) =>
        BreakMode == breakMode &&
        (_exceptionTypeNames.Count == 0 || typeHierarchy.Any(_exceptionTypeNames.Contains));

    /// <summary>
    /// Creates the normalized transport-safe policy represented by this breakpoint.
    /// </summary>
    /// <returns>The immutable managed-exception policy.</returns>
    internal DebugExceptionBreakpointRequest ToRequest() => new(
        BreakMode,
        _exceptionTypeNames.Order(StringComparer.Ordinal).ToArray());

    private static string NormalizeTypeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Trim();
        if (normalized.Length > MaximumTypeNameLength ||
            char.IsDigit(normalized[0]) ||
            normalized.Contains('=', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid managed exception type name '{normalized}'.");
        }

        return normalized;
    }
}
