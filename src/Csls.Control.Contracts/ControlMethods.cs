namespace Csls.Control.Contracts;

/// <summary>
/// Defines the versioned StreamJsonRpc method names for the csls control protocol.
/// </summary>
public static class ControlMethods
{
    /// <summary>
    /// Gets the method that returns the current language-server session.
    /// </summary>
    public const string GetSession = "csls/control/v1/session/get";

    /// <summary>
    /// Gets the method that resolves hover information in the current workspace snapshot.
    /// </summary>
    public const string GetHover = "csls/control/v1/hover/get";

    /// <summary>
    /// Gets the method that returns compiler and analyzer diagnostics for one document.
    /// </summary>
    public const string GetDiagnostics = "csls/control/v1/diagnostics/get";

    /// <summary>
    /// Gets the method that returns bounded completion candidates for one document position.
    /// </summary>
    public const string GetCompletion = "csls/control/v1/completion/get";
}
