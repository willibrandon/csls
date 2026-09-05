using System.Text.Json.Serialization;

namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies how a debugger client should present one variable entry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DebugVariablePresentationKind>))]
public enum DebugVariablePresentationKind
{
    /// <summary>
    /// Presents an ordinary runtime-backed variable.
    /// </summary>
    [JsonStringEnumMemberName("normal")]
    Normal,

    /// <summary>
    /// Presents a debugger-created virtual container.
    /// </summary>
    [JsonStringEnumMemberName("virtual")]
    Virtual,

    /// <summary>
    /// Presents a lazy read-only enumerable view whose expansion executes target code.
    /// </summary>
    [JsonStringEnumMemberName("resultsView")]
    ResultsView,

    /// <summary>
    /// Presents a read-only virtual snapshot whose expansion does not execute target code.
    /// </summary>
    [JsonStringEnumMemberName("resultsSnapshot")]
    ResultsSnapshot,

    /// <summary>
    /// Presents a read-only debugger-generated string message.
    /// </summary>
    [JsonStringEnumMemberName("readOnlyString")]
    ReadOnlyString
}
