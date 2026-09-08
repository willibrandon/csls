using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Retains a copied runtime stack context for resuming forward pages within one stopped generation.
/// </summary>
/// <param name="Identity">The physical activation at the saved context.</param>
/// <param name="Generation">The runtime stop owning the saved registers.</param>
/// <param name="FrameIndex">The activation's zero-based managed stack index.</param>
/// <param name="NativePositionCount">The cumulative native positions visited through this activation.</param>
/// <param name="Context">The bounded context bytes produced by the runtime stack walker.</param>
internal sealed record ManagedStackCheckpoint(
    ManagedFrameIdentity Identity,
    DebugStopGeneration Generation,
    int FrameIndex,
    int NativePositionCount,
    byte[] Context);
