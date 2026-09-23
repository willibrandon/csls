using System.Text.Json.Serialization;

namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects immediate variable children by their container-defined indexing category.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DebugVariableFilter>))]
public enum DebugVariableFilter
{
    /// <summary>
    /// Includes both named and indexed children.
    /// </summary>
    [JsonStringEnumMemberName("all")]
    All,

    /// <summary>
    /// Includes named children such as fields and scope variables.
    /// </summary>
    [JsonStringEnumMemberName("named")]
    Named,

    /// <summary>
    /// Includes indexed children such as array elements.
    /// </summary>
    [JsonStringEnumMemberName("indexed")]
    Indexed
}
