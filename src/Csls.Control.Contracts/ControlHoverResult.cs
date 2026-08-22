using Csls.Protocol;

namespace Csls.Control.Contracts;

/// <summary>
/// Reports whether a control hover request resolved language information.
/// </summary>
public sealed class ControlHoverResult
{
    /// <summary>
    /// Gets a value indicating whether hover information was resolved.
    /// </summary>
    public bool Found { get; init; }

    /// <summary>
    /// Gets the resolved hover information when found.
    /// </summary>
    public Hover? Hover { get; init; }
}
