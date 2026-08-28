namespace Csls.Protocol;

/// <summary>
/// Identifies how one watched workspace file changed.
/// </summary>
public enum FileChangeType
{
    /// <summary>
    /// Indicates that no file-system change was assigned.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates that the file was created.
    /// </summary>
    Created = 1,

    /// <summary>
    /// Indicates that the file contents changed.
    /// </summary>
    Changed = 2,

    /// <summary>
    /// Indicates that the file was deleted.
    /// </summary>
    Deleted = 3
}
