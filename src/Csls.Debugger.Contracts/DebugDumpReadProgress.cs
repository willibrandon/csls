namespace Csls.Debugger.Contracts;

/// <summary>
/// Reports request-local captured-memory read counts and byte totals.
/// </summary>
/// <param name="MemoryReads">The number of completed captured-memory reads.</param>
/// <param name="BytesRead">The number of bytes copied from captured memory.</param>
/// <param name="ContextReads">The number of completed native thread-context reads.</param>
/// <param name="State">The current inspection outcome.</param>
public sealed record DebugDumpReadProgress(long MemoryReads, long BytesRead, long ContextReads, DebugDumpReadState State);
