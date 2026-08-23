using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Groups negotiated server extensions outside the stable LSP capability set.
/// </summary>
public sealed class ExperimentalServerCapabilities
{
    /// <summary>
    /// Gets the C#-specific extension capabilities.
    /// </summary>
    [JsonPropertyName("csharp")]
    public required CSharpExperimentalServerCapabilities CSharp { get; init; }
}
