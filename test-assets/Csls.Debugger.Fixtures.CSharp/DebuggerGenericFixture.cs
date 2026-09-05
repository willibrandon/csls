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

    /// <summary>
    /// Consumes captured generic parameters after asynchronous suspension.
    /// </summary>
    /// <param name="argument">The value inspected and replaced by the debugger.</param>
    /// <param name="replacement">The expected value after debugger assignment.</param>
    /// <param name="unused">An unused source parameter for optimized-storage inspection.</param>
    /// <returns>Zero when execution consumes the assigned parameter.</returns>
    internal async Task<int> RunCapturedAsync(T argument, T replacement, int unused)
    {
        await Task.Yield();
        Console.Write(argument);
        GC.KeepAlive(_value);
        GC.KeepAlive(replacement);
        return EqualityComparer<T>.Default.Equals(argument, replacement) ? 0 : 1;
    }
}
