namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a bounded page of managed modules.
/// </summary>
/// <param name="StartModule">The zero-based first module.</param>
/// <param name="ModuleCount">The maximum count, or zero for all remaining modules.</param>
public sealed record DebugModulesRequest(int StartModule, int ModuleCount);
