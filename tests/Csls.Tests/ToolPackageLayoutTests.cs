using Csls.Support;
using System.IO.Compression;

namespace Csls.Tests;

/// <summary>
/// Rejects malformed native tool packages through real archive files.
/// </summary>
[TestClass]
public sealed class ToolPackageLayoutTests
{
    /// <summary>
    /// Rejects managed entry assemblies and runtime files alongside native tool executables.
    /// </summary>
    [TestMethod]
    [DataRow("csls", "linux-x64", "workers/debugger/csls-debugger-worker.dll")]
    [DataRow("csls-mcp", "win-arm64", "workers/debugger/csls-debugger-worker.dll")]
    [DataRow("csls", "osx-arm64", "csls.dll")]
    [DataRow("csls-mcp", "linux-x64", "csls-mcp.runtimeconfig.json")]
    [DataRow("csls", "linux-arm64", "workers/debugger/libcoreclr.so")]
    [DataRow("csls", "win-x64", "workers/debugger/hostpolicy.dll")]
    public void RejectsManagedHostingArtifactsInNativePackage(
        string commandName,
        string runtimeIdentifier,
        string managedArtifact)
    {
        ArgumentNullException.ThrowIfNull(runtimeIdentifier);
        string path = Path.Join(Path.GetTempPath(), $"csls-invalid-native-package-{Guid.NewGuid():N}.nupkg");
        string root = $"tools/any/{runtimeIdentifier}";
        string extension = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty;
        string entryName = $"{root}/{managedArtifact}";
        try
        {
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry($"{root}/{commandName}{extension}");
                archive.CreateEntry($"{root}/workers/debugger/csls-debugger-worker{extension}");
                archive.CreateEntry(entryName);
            }

            InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
                ToolPackageLayout.ValidateImplementationPackage(
                    path,
                    commandName,
                    runtimeIdentifier,
                    ["workers/debugger/csls-debugger-worker"],
                    native: true));
            Assert.AreEqual(
                $"The Native AOT payload contains a managed hosting artifact: {entryName}",
                exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
