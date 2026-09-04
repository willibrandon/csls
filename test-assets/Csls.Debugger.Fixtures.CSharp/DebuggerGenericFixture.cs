using System.Diagnostics;

namespace Csls.Debugger.Fixtures.CSharp;

/// <summary>
/// Provides a closed generic C# value for debugger construction tests.
/// </summary>
[DebuggerDisplay("generic={_value}", Type = "csharp-generic")]
internal sealed class DebuggerGenericFixture<T>(T value)
{
    private readonly T _value = value;

    /// <summary>
    /// Initializes the generic C# debugger value with its default value.
    /// </summary>
    internal DebuggerGenericFixture()
        : this(default!)
    {
    }

    /// <summary>
    /// Gets the value retained by the constructed instance.
    /// </summary>
    internal T Value => _value;
}
