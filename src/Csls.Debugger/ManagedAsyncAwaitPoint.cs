namespace Csls.Debugger;

/// <summary>
/// Identifies one compiler-recorded asynchronous yield and resumption pair.
/// </summary>
/// <param name="YieldOffset">The state-machine IL offset that begins suspension.</param>
/// <param name="ResumeOffset">The IL offset at which execution resumes.</param>
/// <param name="ResumeMethodToken">The method containing the resumption offset.</param>
internal readonly record struct ManagedAsyncAwaitPoint(
    uint YieldOffset,
    uint ResumeOffset,
    uint ResumeMethodToken);
