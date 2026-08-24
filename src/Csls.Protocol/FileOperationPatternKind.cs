namespace Csls.Protocol;

/// <summary>
/// Defines whether a file-operation glob matches files or folders.
/// </summary>
public static class FileOperationPatternKind
{
    /// <summary>
    /// Matches files only.
    /// </summary>
    public const string File = "file";

    /// <summary>
    /// Matches folders only.
    /// </summary>
    public const string Folder = "folder";
}
