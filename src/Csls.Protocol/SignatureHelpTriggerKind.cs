namespace Csls.Protocol;

/// <summary>
/// Identifies why a client requested signature help.
/// </summary>
public enum SignatureHelpTriggerKind
{
    /// <summary>
    /// Indicates no more specific signature help trigger.
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates an explicit client request.
    /// </summary>
    Invoked = 1,

    /// <summary>
    /// Indicates a configured trigger character.
    /// </summary>
    TriggerCharacter = 2,

    /// <summary>
    /// Indicates changed content while signature help was active.
    /// </summary>
    ContentChange = 3
}
