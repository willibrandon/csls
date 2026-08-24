using Csls.Protocol;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies the source-generated LSP workspace-edit resource-operation representation.
/// </summary>
[TestClass]
public sealed class WorkspaceEditSerializationTests
{
    /// <summary>
    /// Round-trips every stable LSP document-change shape with its concrete type intact.
    /// </summary>
    [TestMethod]
    public void WorkspaceDocumentChangesRoundTripWithoutReflection()
    {
        var firstUri = DocumentUri.FromFileSystemPath(
            Path.Join(Path.GetTempPath(), "First.cs"));
        var secondUri = DocumentUri.FromFileSystemPath(
            Path.Join(Path.GetTempPath(), "Second.cs"));
        var edit = new WorkspaceEdit
        {
            DocumentChanges =
            [
                new CreateFile
                {
                    Uri = firstUri,
                    Options = new CreateFileOptions { IgnoreIfExists = true }
                },
                new TextDocumentEdit
                {
                    TextDocument = new OptionalVersionedTextDocumentIdentifier
                    {
                        Uri = firstUri,
                        Version = null
                    },
                    Edits =
                    [
                        new TextEdit
                        {
                            Range = new LspRange(
                                new Position(0, 0),
                                new Position(0, 0)),
                            NewText = "internal sealed class First;"
                        }
                    ]
                },
                new RenameFile
                {
                    OldUri = firstUri,
                    NewUri = secondUri,
                    Options = new RenameFileOptions { Overwrite = true }
                },
                new DeleteFile
                {
                    Uri = secondUri,
                    Options = new DeleteFileOptions { IgnoreIfNotExists = true }
                }
            ]
        };

        string json = JsonSerializer.Serialize(
            edit,
            LspJsonSerializerContext.Default.WorkspaceEdit);
        WorkspaceEdit roundTrip = JsonSerializer.Deserialize(
            json,
            LspJsonSerializerContext.Default.WorkspaceEdit)
            ?? throw new InvalidDataException("The workspace edit did not deserialize.");

        Assert.HasCount(4, roundTrip.DocumentChanges);
        Assert.IsInstanceOfType<CreateFile>(roundTrip.DocumentChanges[0]);
        Assert.IsInstanceOfType<TextDocumentEdit>(roundTrip.DocumentChanges[1]);
        Assert.IsInstanceOfType<RenameFile>(roundTrip.DocumentChanges[2]);
        Assert.IsInstanceOfType<DeleteFile>(roundTrip.DocumentChanges[3]);
        Assert.Contains("\"kind\":\"create\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"rename\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"delete\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an unknown resource operation instead of accepting an ambiguous edit shape.
    /// </summary>
    [TestMethod]
    public void UnknownWorkspaceResourceOperationIsRejected()
    {
        const string Json = """
            {"documentChanges":[{"kind":"copy","uri":"file:///tmp/Unknown.cs"}]}
            """;

        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(
            Json,
            LspJsonSerializerContext.Default.WorkspaceEdit));
    }
}
