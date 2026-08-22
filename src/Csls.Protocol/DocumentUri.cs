namespace Csls.Protocol;

/// <summary>
/// Represents an absolute URI used by the Language Server Protocol.
/// </summary>
public readonly record struct DocumentUri
{
    private readonly Uri _value;

    private DocumentUri(Uri value)
    {
        _value = value;
    }

    /// <summary>
    /// Parses and validates an absolute document URI.
    /// </summary>
    /// <param name="value">The URI text received from an LSP peer.</param>
    /// <returns>A validated document URI.</returns>
    /// <exception cref="ArgumentException">The value is not an absolute URI.</exception>
    public static DocumentUri Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Document URIs must be absolute.", nameof(value));
        }

        return new DocumentUri(uri);
    }

    /// <summary>
    /// Creates a file document URI from an absolute or relative filesystem path.
    /// </summary>
    /// <param name="path">A filesystem path.</param>
    /// <returns>A normalized absolute file URI.</returns>
    public static DocumentUri FromFileSystemPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DocumentUri(new Uri(Path.GetFullPath(path)));
    }

    /// <summary>
    /// Returns the normalized filesystem path represented by a file URI.
    /// </summary>
    /// <returns>The local filesystem path.</returns>
    /// <exception cref="InvalidOperationException">The document URI is not a file URI.</exception>
    public string GetFileSystemPath()
    {
        if (!_value.IsFile)
        {
            throw new InvalidOperationException("Only file document URIs have filesystem paths.");
        }

        return Path.GetFullPath(_value.LocalPath);
    }

    /// <inheritdoc />
    public override string ToString() => _value.AbsoluteUri;
}
