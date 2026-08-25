using System.Text.Json.Serialization;

namespace Csls.Protocol;

/// <summary>
/// Represents one typed value carried by an LSP work-done progress notification.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WorkDoneProgressBegin), "begin")]
[JsonDerivedType(typeof(WorkDoneProgressReport), "report")]
[JsonDerivedType(typeof(WorkDoneProgressEnd), "end")]
public abstract record WorkDoneProgressValue;
