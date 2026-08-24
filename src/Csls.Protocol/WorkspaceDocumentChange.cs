using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Represents one ordered text edit or filesystem resource operation in a workspace edit.
/// </summary>
[JsonConverter(typeof(WorkspaceDocumentChangeJsonConverter))]
public abstract record WorkspaceDocumentChange;
