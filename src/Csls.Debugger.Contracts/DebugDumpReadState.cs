namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes the current outcome of one captured-frame inspection.
/// </summary>
public enum DebugDumpReadState
{
    /// <summary>
    /// Captured memory or thread contexts are being read by CoreCLR.
    /// </summary>
    Reading,

    /// <summary>
    /// The requested frame values have been recovered and temporary native references released.
    /// </summary>
    Completed,

    /// <summary>
    /// The caller canceled inspection and temporary native references have been released.
    /// </summary>
    Canceled,

    /// <summary>
    /// Inspection failed and temporary native references have been released.
    /// </summary>
    Failed
}
