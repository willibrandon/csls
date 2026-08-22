namespace Csls.Core;

/// <summary>
/// Specifies the concurrency and workspace-mutation semantics of a request.
/// </summary>
public enum RequestMode
{
    /// <summary>
    /// Runs concurrently against an immutable workspace snapshot.
    /// </summary>
    ReadOnly,

    /// <summary>
    /// Waits for prior reads and runs exclusively with other mutations.
    /// </summary>
    ReadWrite,

    /// <summary>
    /// Runs concurrently without delaying foreground reads or writes.
    /// </summary>
    ReadOnlyBackground
}
