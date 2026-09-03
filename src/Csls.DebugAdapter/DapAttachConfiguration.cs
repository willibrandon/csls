namespace Csls.DebugAdapter;

/// <summary>
/// Contains validated process and runtime options for one DAP attach request.
/// </summary>
/// <param name="ProcessId">The positive operating-system process identifier.</param>
/// <param name="JustMyCode">Whether source stepping excludes non-user managed code.</param>
/// <param name="EnableStepFiltering">Whether stepping skips properties and operators.</param>
internal sealed record DapAttachConfiguration(
    int ProcessId,
    bool JustMyCode,
    bool EnableStepFiltering);
