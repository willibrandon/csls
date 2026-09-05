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

    /// <summary>
    /// Preserves declared parameter metadata while optimized state machines discard unused storage.
    /// </summary>
    /// <typeparam name="TMethod">The independently closed method parameter type.</typeparam>
    /// <typeparam name="TMatrix">The closed rectangular-array parameter type.</typeparam>
    /// <typeparam name="TOffsetArray">The closed non-vector array parameter type.</typeparam>
    /// <param name="typeArgument">The parameter closed over the declaring type.</param>
    /// <param name="methodArgument">The parameter closed over the method.</param>
    /// <param name="vector">The primitive vector parameter.</param>
    /// <param name="rectangle">The rectangular reference-array parameter.</param>
    /// <param name="nullable">The nullable primitive parameter.</param>
    /// <param name="pair">The parameter with authored tuple element names.</param>
    /// <param name="nested">The nested generic, nullable, and array parameter.</param>
    /// <param name="jagged">The nested vector parameter.</param>
    /// <param name="amount">The decimal parameter.</param>
    /// <param name="offsetArray">The non-vector array with a nonzero lower bound.</param>
    /// <returns>Zero after the debugger inspects the original declarations.</returns>
    internal static async Task<int> RunUnusedShapesAsync<TMethod, TMatrix, TOffsetArray>(
        T typeArgument, TMethod methodArgument, int[] vector, TMatrix rectangle,
        int? nullable, (int Count, string Name) pair,
        Dictionary<string, List<int?[]>> nested, T[][] jagged, decimal amount, TOffsetArray offsetArray)
    {
        await Task.Yield();
        Console.Write(42);
        return 0;
    }
}
