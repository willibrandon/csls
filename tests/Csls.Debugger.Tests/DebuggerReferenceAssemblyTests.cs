using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies compiler-produced F# reference identities across incremental version changes.
/// </summary>
[TestClass]
public sealed class DebuggerReferenceAssemblyTests
{
    private static readonly string[] s_fixtureFiles = ["DebuggerGenericFixture.fs", "DebuggerFixtureValue.fs", "Program.fs"];

    /// <summary>
    /// Gets the framework-owned cancellation context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Publishes the new compiler-produced reference assembly after a version change and preserves a no-op build.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task IncrementalFSharpBuildPreservesReferenceAssemblyIdentity()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-fsharp-reference-identity-");
        try
        {
            string repository = DebuggerTestEnvironment.FindRepositoryRoot();
            string fixture = Path.Join(repository, "test-assets", "Csls.Debugger.Fixtures.FSharp");
            string projectPath = Path.Join(directory.FullName, "Csls.ReferenceIdentity.fsproj");
            var project = new XDocument(new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("OutputType", "Exe"),
                    new XElement("Nullable", "enable"),
                    new XElement("LangVersion", "latest"),
                    new XElement("Deterministic", "true"),
                    new XElement("TreatWarningsAsErrors", "true"),
                    new XElement("ProduceReferenceAssembly", "true")),
                new XElement("ItemGroup",
                    s_fixtureFiles.Select(name => new XElement("Compile", new XAttribute("Include", Path.Join(fixture, name))))),
                new XElement("Import", new XAttribute("Project",
                    Path.Join(repository, "build", "Csls.FSharp.ReferenceAssembly.targets")))));
            await File.WriteAllTextAsync(projectPath, project.ToString(), TestContext.CancellationToken).ConfigureAwait(false);

            string intermediate = Path.Join(directory.FullName, "obj", "Debug", "net10.0");
            string reference = Path.Join(intermediate, "ref", "Csls.ReferenceIdentity.dll");
            string emitted = Path.Join(intermediate, "refint", "Csls.ReferenceIdentity.dll");
            string implementation = Path.Join(directory.FullName, "bin", "Debug", "net10.0", "Csls.ReferenceIdentity.dll");
            await BuildAsync(projectPath, "0.2.0").ConfigureAwait(false);
            Assert.AreEqual(new Version(0, 2, 0, 0), ReadVersion(reference));
            await BuildAsync(projectPath, "0.1.0").ConfigureAwait(false);
            Assert.AreEqual(new Version(0, 1, 0, 0), ReadVersion(implementation));
            Assert.AreEqual(ReadVersion(implementation), ReadVersion(reference));
            byte[] expected = await File.ReadAllBytesAsync(emitted, TestContext.CancellationToken).ConfigureAwait(false);
            byte[] actual = await File.ReadAllBytesAsync(reference, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreSequenceEqual(expected, actual);

            DateTime referenceWrite = File.GetLastWriteTimeUtc(reference);
            DateTime implementationWrite = File.GetLastWriteTimeUtc(implementation);
            await BuildAsync(projectPath, "0.1.0").ConfigureAwait(false);
            Assert.AreEqual(referenceWrite, File.GetLastWriteTimeUtc(reference));
            Assert.AreEqual(implementationWrite, File.GetLastWriteTimeUtc(implementation));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
    }

    private async Task BuildAsync(string project, string version)
    {
        string results = Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(), "artifacts", "test-results");
        Directory.CreateDirectory(results);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!
        };
        foreach (string argument in new[] { "build", project, "--nologo", "--disable-build-servers", $"-p:Version={version}" })
        {
            start.ArgumentList.Add(argument);
        }

        string binlog = Path.Join(results, $"reference-identity-{Guid.NewGuid():N}.binlog");
        start.ArgumentList.Add($"-bl:{binlog}");
        try
        {
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(start, TestContext.CancellationToken,
                line => TestContext.WriteLine($"Reference build {version}: {line}"))
                .ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, $"{output}{Environment.NewLine}{error}");
        }
        finally
        {
            if (File.Exists(binlog))
            {
                TestContext.AddResultFile(binlog);
            }
        }
    }

    private static Version ReadVersion(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        return pe.GetMetadataReader().GetAssemblyDefinition().Version;
    }
}
