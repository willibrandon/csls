namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects whether symbol loading uses an exclusion or inclusion list.
/// </summary>
public enum DebugSymbolModuleFilterMode
{
    /// <summary>
    /// Loads symbols for every module except matching exclusions.
    /// </summary>
    LoadAllButExcluded,

    /// <summary>
    /// Loads symbols only for modules matching an explicit inclusion.
    /// </summary>
    LoadOnlyIncluded
}
