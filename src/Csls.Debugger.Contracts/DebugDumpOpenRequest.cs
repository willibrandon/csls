namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a managed process dump and one runtime for read-only inspection.
/// </summary>
/// <param name="DumpPath">The absolute existing process-dump path.</param>
/// <param name="RuntimeIndex">The zero-based managed runtime index.</param>
/// <param name="DacPath">An optional absolute matching DAC path.</param>
public sealed record DebugDumpOpenRequest(
    string DumpPath,
    int RuntimeIndex = 0,
    string? DacPath = null);
