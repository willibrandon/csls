using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Declares an incompatible open proxy that must not run.
/// </summary>
/// <typeparam name="T">The physical value type.</typeparam>
[DebuggerTypeProxy(typeof(ArityMismatchDebuggerTypeProxyFixtureProxy<,>))]
internal sealed class ArityMismatchDebuggerTypeProxyFixture<T>
{
    /// <summary>
    /// Creates an arity-mismatch proxy target.
    /// </summary>
    /// <param name="value">The physical value.</param>
    internal ArityMismatchDebuggerTypeProxyFixture(T value) => Value = value;

    /// <summary>
    /// Gets the physical value preserved by ordinary expansion.
    /// </summary>
    public readonly T Value;
}
