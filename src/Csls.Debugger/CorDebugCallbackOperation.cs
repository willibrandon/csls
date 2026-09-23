namespace Csls.Debugger;

/// <summary>
/// Represents actor-serialized work retained from one managed runtime callback.
/// </summary>
/// <param name="target">The callback object that owns runtime services.</param>
/// <param name="thread">The retained callback thread, or zero.</param>
/// <param name="subject">The retained primary callback subject, or zero.</param>
/// <param name="auxiliary">The retained secondary callback subject, or zero.</param>
/// <param name="cancellationToken">The engine actor cancellation token.</param>
/// <returns>Whether the callback should resume the target.</returns>
internal delegate ValueTask<bool> CorDebugCallbackOperation(
    CorDebugManagedCallback target,
    nint thread,
    nint subject,
    nint auxiliary,
    CancellationToken cancellationToken);
