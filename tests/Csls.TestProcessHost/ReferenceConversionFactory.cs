namespace Csls.TestProcessHost;

/// <summary>
/// Produces references whose declared return type differs from their current runtime value.
/// </summary>
/// <typeparam name="T">The closed reference type used by the generic return declaration.</typeparam>
internal sealed class ReferenceConversionFactory<T>
    where T : class
{
    private readonly T _value;

    /// <summary>
    /// Creates a factory retaining the original object through its generic declaration.
    /// </summary>
    internal ReferenceConversionFactory(T value)
    {
        _value = value;
    }

    /// <summary>
    /// Records explicitly executed calls so assignment tests can distinguish evaluation from mutation.
    /// </summary>
    internal int _calls;

    /// <summary>
    /// Returns a concrete derived object through a base return declaration.
    /// </summary>
    internal Exception GetValue()
    {
        _calls++;
        return new ArgumentException("evaluated replacement");
    }

    /// <summary>
    /// Returns a typed null reference through the same base declaration.
    /// </summary>
    internal Exception? GetNull()
    {
        _calls++;
        return null;
    }

    /// <summary>
    /// Returns the retained object using the containing type's generic parameter.
    /// </summary>
    internal T GetGenericValue()
    {
        _calls++;
        return _value;
    }
}
