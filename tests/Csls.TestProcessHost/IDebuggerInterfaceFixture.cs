namespace Csls.TestProcessHost;

/// <summary>
/// Defines interface-dispatched operations used by the real debugger target.
/// </summary>
/// <typeparam name="T">The operation value type.</typeparam>
internal interface IDebuggerInterfaceFixture<T>
{
    /// <summary>
    /// Transforms one value through the implementing target type.
    /// </summary>
    /// <param name="value">The input value.</param>
    /// <returns>The implementation-specific transformed value.</returns>
    int Transform(T value);

    /// <summary>
    /// Transforms one value through a default interface implementation.
    /// </summary>
    /// <param name="value">The input value.</param>
    /// <returns>The default-interface transformed value.</returns>
    int DefaultTransform(T value) => value is int number ? number + 300 : -1;

    /// <summary>
    /// Preserves a separately inferred value through a generic default method.
    /// </summary>
    /// <typeparam name="TResult">The inferred value type.</typeparam>
    /// <param name="value">The value to preserve.</param>
    /// <returns>The original value.</returns>
    TResult Echo<TResult>(TResult value) => value;
}
