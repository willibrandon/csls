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
    /// Gets whether values expose physical fields instead of debugger display attributes and visualizers.
    /// </summary>
    [Description("Show physical runtime fields for objects, tuples, and collections.")]
    [DefaultValue(false)]
    public bool ShowRawValues { get; init; }
}
