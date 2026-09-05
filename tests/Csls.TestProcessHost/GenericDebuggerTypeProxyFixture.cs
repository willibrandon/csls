using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides a closed generic target for open debugger proxy substitution.
/// </summary>
/// <typeparam name="T">The exact runtime value type.</typeparam>
[DebuggerTypeProxy(typeof(GenericDebuggerTypeProxyFixtureProxy<>))]
internal sealed class GenericDebuggerTypeProxyFixture<T>
{
    /// <summary>
    /// Creates a generic debugger proxy target.
    /// </summary>
    /// <param name="value">The retained physical value.</param>
    internal GenericDebuggerTypeProxyFixture(T value) => _rawValue = value;

    /// <summary>
    /// Stores the physical generic value exposed through Raw View.
    /// </summary>
    internal readonly T _rawValue;
}
