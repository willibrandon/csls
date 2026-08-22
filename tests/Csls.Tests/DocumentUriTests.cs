using Csls.Protocol;

namespace Csls.Tests;

/// <summary>
/// Verifies document URI behavior through the production value type.
/// </summary>
[TestClass]
public sealed class DocumentUriTests
{
    /// <summary>
    /// Verifies filesystem paths survive URI encoding and decoding.
    /// </summary>
    [TestMethod]
    public void FileSystemPathRoundTripsReservedAndUnicodeCharacters()
    {
        var path = Path.GetFullPath(Path.Combine("fixture root", "résumé #1.cs"));

        var uri = DocumentUri.FromFileSystemPath(path);

        Assert.AreEqual(path, uri.GetFileSystemPath());
        Assert.Contains("r%C3%A9sum%C3%A9%20%231.cs", uri.ToString());
    }

    /// <summary>
    /// Verifies relative URI text is rejected.
    /// </summary>
    [TestMethod]
    public void ParseRejectsRelativeUri()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            DocumentUri.Parse("src/Program.cs"));

        Assert.Contains("absolute", exception.Message);
    }

    /// <summary>
    /// Verifies non-file URIs cannot be converted to filesystem paths.
    /// </summary>
    [TestMethod]
    public void NonFileUriCannotBecomeFileSystemPath()
    {
        var uri = DocumentUri.Parse("csharp:/metadata/System.String.cs");

        var exception = Assert.ThrowsExactly<InvalidOperationException>(uri.GetFileSystemPath);

        Assert.AreEqual("Only file document URIs have filesystem paths.", exception.Message);
    }
}
