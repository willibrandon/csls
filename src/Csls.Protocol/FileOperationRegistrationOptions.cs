namespace Csls.Protocol;

/// <summary>
/// Registers the workspace paths relevant to one file-operation method.
/// </summary>
public sealed record FileOperationRegistrationOptions
{
    /// <summary>
    /// Gets the ordered file-operation filters evaluated by the client.
    /// </summary>
    public required IReadOnlyList<FileOperationFilter> Filters { get; init; }
}
