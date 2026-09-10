namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries a page of loaded managed modules and the complete module count.
/// </summary>
/// <param name="Modules">The requested ordered module page.</param>
/// <param name="TotalModules">The number of loaded modules before paging.</param>
public sealed record DebugModulePage(
    IReadOnlyList<DebugModuleInfo> Modules,
    int TotalModules);
