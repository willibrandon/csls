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
        string path = Path.GetFullPath(Path.Join("fixture root", "résumé #1.cs"));

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
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
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

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            uri.GetFileSystemPath);

        Assert.AreEqual("Only file document URIs have filesystem paths.", exception.Message);
    }

    /// <summary>
    /// Verifies percent-encoded Windows drive roots do not become duplicated relative segments.
    /// </summary>
    [TestMethod]
    public void EncodedWindowsDriveRootIsNormalizedOnce()
    {
        var uri = DocumentUri.Parse("file:///C%3A/Users/editor/Project/Program.cs");

        string path = uri.GetFileSystemPath();

        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(@"C:\Users\editor\Project\Program.cs", path);
        }
        else
        {
            Assert.AreEqual("/C:/Users/editor/Project/Program.cs", path);
        }
    }
}
