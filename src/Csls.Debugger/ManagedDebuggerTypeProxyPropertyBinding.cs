using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns one resolved proxy property getter and its debugger presentation metadata.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyPropertyBinding
{
    private nint _function;

    /// <summary>
    /// Creates an owned proxy property getter binding.
    /// </summary>
    /// <param name="name">The debugger-facing property name.</param>
    /// <param name="declaredType">The metadata return-type display.</param>
    /// <param name="browsingState">The declared debugger browsing policy.</param>
    /// <param name="isStatic">Whether the getter is static.</param>
    /// <param name="function">The owned ICorDebugFunction pointer.</param>
    internal ManagedDebuggerTypeProxyPropertyBinding(
        string name,
        string declaredType,
        ManagedDebuggerBrowsableState browsingState,
        bool isStatic,
        nint function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredType);
        ArgumentOutOfRangeException.ThrowIfZero(function);
        Name = name;
        DeclaredType = declaredType;
        BrowsingState = browsingState;
        IsStatic = isStatic;
        _function = function;
    }

    /// <summary>
    /// Gets the debugger-facing property name.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the metadata return-type display.
    /// </summary>
    internal string DeclaredType { get; }

    /// <summary>
    /// Gets the debugger browsing policy declared on the property.
    /// </summary>
    internal ManagedDebuggerBrowsableState BrowsingState { get; }

    /// <summary>
    /// Gets whether the getter is static.
    /// </summary>
    internal bool IsStatic { get; }

    /// <summary>
    /// Transfers the owned ICorDebugFunction pointer to an active evaluation.
    /// </summary>
    /// <returns>The owned ICorDebugFunction pointer.</returns>
    internal nint DetachFunction()
    {
        nint function = _function;
        _function = 0;
        return function != 0
            ? function
            : throw new InvalidOperationException(
                $"Proxy property '{Name}' no longer owns its getter function.");
    }

    /// <summary>
    /// Releases the getter function when it was not transferred to an evaluation.
    /// </summary>
    internal void Release()
    {
        nint function = Interlocked.Exchange(ref _function, 0);
        if (function != 0)
        {
            _ = ComAbi.Release(function);
        }
    }
}
