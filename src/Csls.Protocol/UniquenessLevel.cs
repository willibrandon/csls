using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Identifies the scope in which a moniker identifier is unique.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UniquenessLevel>))]
public enum UniquenessLevel
{
    /// <summary>
    /// Indicates that the identifier is unique within one document.
    /// </summary>
    [JsonStringEnumMemberName("document")]
    Document,

    /// <summary>
    /// Indicates that the identifier is unique within one project.
    /// </summary>
    [JsonStringEnumMemberName("project")]
    Project,

    /// <summary>
    /// Indicates that the identifier is unique within one related project group.
    /// </summary>
    [JsonStringEnumMemberName("group")]
    Group,

    /// <summary>
    /// Indicates that the identifier is unique within its moniker scheme.
    /// </summary>
    [JsonStringEnumMemberName("scheme")]
    Scheme,

    /// <summary>
    /// Indicates that the identifier is globally unique across schemes.
    /// </summary>
    [JsonStringEnumMemberName("global")]
    Global
}
