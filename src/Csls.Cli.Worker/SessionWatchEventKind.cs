using System.Text.Json.Serialization;

namespace Csls.Cli.Worker;

/// <summary>
/// Identifies how one live-session watch observation changed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionWatchEventKind>))]
internal enum SessionWatchEventKind
{
    /// <summary>
    /// Provides the complete live-session state when watching begins.
    /// </summary>
    Snapshot,

    /// <summary>
    /// Indicates that a live language-server session appeared.
    /// </summary>
    Added,

    /// <summary>
    /// Indicates that an existing live language-server session changed.
    /// </summary>
    Updated,

    /// <summary>
    /// Indicates that a live language-server session disappeared.
    /// </summary>
    Removed
}
