using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Identifies how a moniker-bearing symbol participates in the current project.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MonikerKind>))]
public enum MonikerKind
{
    /// <summary>
    /// Identifies a symbol imported from another project or assembly.
    /// </summary>
    [JsonStringEnumMemberName("import")]
    Import,

    /// <summary>
    /// Identifies a symbol exported by the current project.
    /// </summary>
    [JsonStringEnumMemberName("export")]
    Export,

    /// <summary>
    /// Identifies a symbol local to the current project or document.
    /// </summary>
    [JsonStringEnumMemberName("local")]
    Local
}
