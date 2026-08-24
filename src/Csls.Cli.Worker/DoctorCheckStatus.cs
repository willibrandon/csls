using System.Text.Json.Serialization;

namespace Csls.Cli.Worker;

/// <summary>
/// Defines the outcome of one workspace doctor check.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DoctorCheckStatus>))]
internal enum DoctorCheckStatus
{
    /// <summary>
    /// Indicates that the checked capability is working.
    /// </summary>
    Pass,

    /// <summary>
    /// Indicates source state worth reviewing that does not break csls startup.
    /// </summary>
    Warning,

    /// <summary>
    /// Indicates that a required csls capability did not work.
    /// </summary>
    Fail
}
