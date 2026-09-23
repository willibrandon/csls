using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects the presentation of values inspected in a managed debugger session.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DebugExpressionEvaluationOptions
{
    /// <summary>
    /// Creates the automatic evaluation and value presentation policy for a managed session.
    /// </summary>
    /// <param name="allowImplicitFuncEval">Whether authorized inspection can automatically execute debugger proxies.</param>
    /// <param name="showRawValues">Whether inspection exposes physical fields using runtime formatting.</param>
    public DebugExpressionEvaluationOptions(bool allowImplicitFuncEval = true, bool showRawValues = false)
    {
        AllowImplicitFuncEval = allowImplicitFuncEval;
        ShowRawValues = showRawValues;
    }

    /// <summary>
    /// Gets whether authorized value inspection automatically constructs debugger proxies and evaluates their properties.
    /// </summary>
    [Description("Allow automatic debugger proxy construction and property evaluation during authorized inspection.")]
    public bool AllowImplicitFuncEval { get; init; }

    /// <summary>
    /// Gets whether values expose physical fields instead of debugger display attributes and visualizers.
    /// </summary>
    [Description("Show physical runtime fields for objects, tuples, and collections.")]
    public bool ShowRawValues { get; init; }
}
