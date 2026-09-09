using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies the real editor archives consumed by the desktop and remote CI jobs.
/// </summary>
[TestClass]
[TestCategory("VsCodeHost")]
public sealed class VsCodeTestAssetTests
{
    /// <summary>
    /// Requires the published editor and its completion marker without accumulated cached releases.
    /// </summary>
    [TestMethod]
    public void CommonArchiveContainsSelectedEditorOnly()
    {
        (HashSet<string> paths, Dictionary<string, string> metadata) = ReadArchive("common");
        const string Cache = "artifacts/tools/vscode/stable/";
        const string Manifest = Cache + "executable.path";
        string executable = metadata[Manifest];
        string installation = Cache + executable.Split('/')[0];
        Assert.Contains(Cache + executable, paths);
        Assert.Contains(installation + "/is-complete", paths);
        Assert.IsNotEmpty(metadata[installation + "/resources/app/product.json"]);
        foreach (string path in paths.Where(path => IsUnder(path, "artifacts/tools/vscode")))
        {
            Assert.IsTrue(path == Manifest || IsUnder(path, installation) || IsUnder(installation, path),
                $"Unselected editor asset: {path}");
        }
        AssertCurrentExtension(paths, "vscode-dotnet-runtime");
    }

    /// <summary>
    /// Ships only the current C# extension and Dev Kit packages to desktop oracle tests.
    /// </summary>
    [TestMethod]
    public void DesktopArchiveContainsCurrentExtensionsOnly()
    {
        (HashSet<string> paths, _) = ReadArchive("desktop");
        AssertCurrentExtension(paths, "vscode-csharp");
        AssertCurrentExtension(paths, "vscode-csdevkit");
        Assert.DoesNotContain(path => IsUnder(path, "artifacts/tools/vscode-server"), paths);
    }

    /// <summary>
    /// Ships exactly the server revision paired with the editor selected in the common archive.
    /// </summary>
    [TestMethod]
    public void RemoteArchiveMatchesSelectedEditor()
    {
        (_, Dictionary<string, string> common) = ReadArchive("common");
        const string Cache = "artifacts/tools/vscode/stable/";
        string installation = Cache + common[Cache + "executable.path"].Split('/')[0];
        string revision = common[installation + "/resources/app/product.json"];
        string server = $"artifacts/tools/vscode-server/{revision}/linux-x64";
        (HashSet<string> paths, Dictionary<string, string> metadata) = ReadArchive("remote");
        Assert.AreEqual(revision, metadata[server + "/product.json"]);
        Assert.Contains(server + "/node", paths);
        Assert.Contains(server + "/bin/code-server", paths);
        Assert.Contains(server + "/out/server-main.js", paths);
        foreach (string path in paths)
        {
            Assert.IsTrue(IsUnder(path, server) || IsUnder(server, path), $"Unselected remote server asset: {path}");
        }
    }

    private static void AssertCurrentExtension(HashSet<string> paths, string name)
    {
        string root = "artifacts/tools/" + name;
        string current = root + "/current";
        string[] packages = [.. paths.Where(path => IsUnder(path, root))];
        Assert.IsNotEmpty(packages);
        Assert.Contains(path => path.EndsWith(".vsix", StringComparison.Ordinal), packages, name);
        foreach (string path in packages)
        {
            Assert.IsTrue(IsUnder(path, current) || path == root, $"Unselected extension asset: {path}");
        }
    }

    private static bool IsUnder(string path, string directory) =>
        path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static (HashSet<string> Paths, Dictionary<string, string> Metadata) ReadArchive(string group)
    {
        string root = Environment.GetEnvironmentVariable("CSLS_VSCODE_TEST_ASSET_DIRECTORY")
            ?? Path.Join(EditorToolResolver.FindRepositoryRoot(), "artifacts");
        string path = Path.Join(root, $"vscode-test-assets-{group}.tar");
        if (string.Equals(Environment.GetEnvironmentVariable("CSLS_REQUIRE_VSCODE_TEST_ASSETS"), "true", StringComparison.Ordinal))
        {
            Assert.IsTrue(File.Exists(path), $"Required CI editor archive is missing: {path}");
        }
        TestPrerequisite.RequireFile(path, "Pack editor assets with scripts/Prepare-VsCodeTestAssets.cs pack.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        using FileStream stream = File.OpenRead(path);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            string name = entry.Name.TrimEnd('/');
            Assert.IsTrue(paths.Add(name), $"Duplicate archive entry: {name}");
            if (name == "artifacts/tools/vscode/stable/executable.path")
            {
                Assert.IsNotNull(entry.DataStream);
                using var text = new StreamReader(entry.DataStream, Encoding.UTF8, leaveOpen: true);
                metadata.Add(name, text.ReadToEnd());
            }
            else if (name.EndsWith("/resources/app/product.json", StringComparison.Ordinal) ||
                name.StartsWith("artifacts/tools/vscode-server/", StringComparison.Ordinal) &&
                name.EndsWith("/product.json", StringComparison.Ordinal) && name.Split('/').Length == 6)
            {
                Assert.IsNotNull(entry.DataStream);
                using var product = JsonDocument.Parse(entry.DataStream);
                metadata.Add(name, product.RootElement.GetProperty("commit").GetString()
                    ?? throw new InvalidDataException("The actual editor package has no commit."));
            }
        }

        return (paths, metadata);
    }
}
