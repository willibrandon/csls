namespace Csls.Debugger;

/// <summary>
/// Owns one evaluated proxy property result until final-generation publication.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyPropertyResult
{
    private nint _handle;

    /// <summary>
    /// Creates one evaluated proxy property result.
    /// </summary>
    /// <param name="name">The debugger-facing property name.</param>
    /// <param name="browsingState">The declared debugger browsing policy.</param>
    /// <param name="isStatic">Whether the property is static.</param>
    /// <param name="display">The immediate formatted property value.</param>
    /// <param name="isError">Whether the getter failed to produce an ordinary value.</param>
    /// <param name="handle">The optional owned strong runtime handle.</param>
    internal ManagedDebuggerTypeProxyPropertyResult(
        string name,
        ManagedDebuggerBrowsableState browsingState,
        bool isStatic,
        ManagedValueDisplay display,
        bool isError,
        nint handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        BrowsingState = browsingState;
        IsStatic = isStatic;
        Display = display;
        IsError = isError;
        _handle = handle;
    }

    /// <summary>
    /// Gets the debugger-facing property name.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the declared debugger browsing policy.
    /// </summary>
    internal ManagedDebuggerBrowsableState BrowsingState { get; }

    /// <summary>
    /// Gets whether the property is static.
    /// </summary>
    internal bool IsStatic { get; }

    /// <summary>
    /// Gets the immediate formatted property value.
    /// </summary>
    internal ManagedValueDisplay Display { get; }

    /// <summary>
    /// Gets whether the getter failed to produce an ordinary value.
    /// </summary>
    internal bool IsError { get; }

    /// <summary>
    /// Transfers the optional strong runtime handle for final publication.
    /// </summary>
    /// <returns>The owned strong runtime handle, or zero.</returns>
    internal nint DetachHandle()
    {
        nint handle = _handle;
        _handle = 0;
        return handle;
    }

}
