namespace Csls.Debugger;

/// <summary>
/// Carries tuple-name transforms for one exact declared type construction.
/// </summary>
internal sealed class ManagedTupleCustomTypeInfo : IEquatable<ManagedTupleCustomTypeInfo>
{
    /// <summary>
    /// Creates immutable tuple-name transform metadata.
    /// </summary>
    /// <param name="transformNames">The pre-order tuple-name transform sequence.</param>
    internal ManagedTupleCustomTypeInfo(IEnumerable<string?> transformNames)
    {
        ArgumentNullException.ThrowIfNull(transformNames);
        TransformNames = [.. transformNames];
    }

    /// <summary>
    /// Gets the pre-order tuple-name transform sequence.
    /// </summary>
    internal IReadOnlyList<string?> TransformNames { get; }

    /// <summary>
    /// Compares authored tuple names across equivalent metadata reads.
    /// </summary>
    /// <param name="other">The other tuple-name transform.</param>
    /// <returns>Whether each transform position has the same name.</returns>
    public bool Equals(ManagedTupleCustomTypeInfo? other) =>
        other is not null && TransformNames.SequenceEqual(other.TransformNames, StringComparer.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ManagedTupleCustomTypeInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (string? name in TransformNames)
        {
            hash.Add(name, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Creates metadata for one contiguous nested type construction.
    /// </summary>
    /// <param name="start">The zero-based first transform name.</param>
    /// <param name="count">The number of transform names.</param>
    /// <returns>The nested metadata, or null when its segment has no authored names.</returns>
    internal ManagedTupleCustomTypeInfo? Slice(int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            count,
            TransformNames.Count,
            nameof(count));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            start,
            TransformNames.Count - count,
            nameof(start));

        string?[] names = [.. TransformNames.Skip(start).Take(count)];
        return names.Any(static name => !string.IsNullOrEmpty(name))
            ? new ManagedTupleCustomTypeInfo(names)
            : null;
    }
}
