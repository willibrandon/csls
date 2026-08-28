using Csls.Protocol;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Csls.Web.Worker;

/// <summary>
/// Reads browser LSP parameters without runtime serialization metadata.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class BrowserLspParameterReader
{
    /// <summary>
    /// Reads an opened text document notification.
    /// </summary>
    /// <param name="parameters">The serialized parameter object.</param>
    /// <returns>The typed notification parameters.</returns>
    internal static DidOpenTextDocumentParams ReadDidOpen(JSObject? parameters)
    {
        using JSObject textDocument = GetRequiredObject(parameters, "textDocument");
        return new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = DocumentUri.Parse(GetRequiredString(textDocument, "uri")),
                LanguageId = GetRequiredString(textDocument, "languageId"),
                Version = textDocument.GetPropertyAsInt32("version"),
                Text = GetRequiredString(textDocument, "text")
            }
        };
    }

    /// <summary>
    /// Reads a text document position request.
    /// </summary>
    /// <param name="parameters">The serialized parameter object.</param>
    /// <returns>The typed request parameters.</returns>
    internal static TextDocumentPositionParams ReadTextDocumentPosition(JSObject? parameters)
    {
        using JSObject textDocument = GetRequiredObject(parameters, "textDocument");
        using JSObject position = GetRequiredObject(parameters, "position");
        return new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.Parse(GetRequiredString(textDocument, "uri"))
            },
            Position = new Position(
                position.GetPropertyAsInt32("line"),
                position.GetPropertyAsInt32("character"))
        };
    }

    private static JSObject GetRequiredObject(JSObject? parent, string name)
    {
        return parent?.GetPropertyAsJSObject(name)
            ?? throw new InvalidDataException(
                $"The required '{name}' property was not provided.");
    }

    private static string GetRequiredString(JSObject parent, string name)
    {
        return parent.GetPropertyAsString(name)
            ?? throw new InvalidDataException(
                $"The required '{name}' property was not provided.");
    }
}
