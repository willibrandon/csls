namespace Csls.Debugger;

/// <summary>
/// Selects the debugger presentation applied while expanding a retained value.
/// </summary>
internal enum ManagedValueView
{
    /// <summary>
    /// Applies debugger presentation metadata from the target type.
    /// </summary>
    Default,

    /// <summary>
    /// Applies ordinary presentation metadata while suppressing another proxy construction.
    /// </summary>
    ProxyBypassed,

    /// <summary>
    /// Exposes eagerly materialized static members of a constructed debugger proxy.
    /// </summary>
    ProxyStatics,

    /// <summary>
    /// Exposes the target's physical runtime fields without presentation metadata.
    /// </summary>
    Raw,

    /// <summary>
    /// Exposes a proxied target's physical fields while suppressing enumerable presentation.
    /// </summary>
    ProxyRaw,

    /// <summary>
    /// Requires explicit target execution to materialize an enumerable's elements.
    /// </summary>
    ResultsView,

    /// <summary>
    /// Exposes the retained result of an explicitly requested enumeration.
    /// </summary>
    ResultsMaterialized
}
