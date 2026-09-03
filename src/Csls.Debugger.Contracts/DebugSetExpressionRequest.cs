namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects one writable source expression for an exact stopped-generation assignment.
/// </summary>
/// <param name="StopGeneration">The exact stopped generation authorizing the write.</param>
/// <param name="FrameId">The generation-bound managed frame.</param>
/// <param name="Expression">The writable source expression.</param>
/// <param name="Value">The side-effect-free source-language value expression.</param>
public sealed record DebugSetExpressionRequest(
    long StopGeneration,
    int FrameId,
    string Expression,
    string Value);
