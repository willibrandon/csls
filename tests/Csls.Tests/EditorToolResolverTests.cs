using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Tests;

/// <summary>
/// Verifies the build identity of real assemblies selected for integration tests.
/// </summary>
[TestClass]
public sealed class EditorToolResolverTests
{
    /// <summary>
    /// Selects launcher and worker assemblies with the same configuration as the running tests.
    /// </summary>
    /// <param name="projectName">The project whose compiled assembly is inspected.</param>
    /// <param name="assemblyFileName">The corresponding managed assembly file name.</param>
    [TestMethod]
    [DataRow("Csls.App", "csls.dll")]
    [DataRow("Csls.Worker", "csls-worker.dll")]
    [DataRow("Csls.Mcp", "csls-mcp.dll")]
    [DataRow("Csls.Mcp.Worker", "csls-mcp-worker.dll")]
    [DataRow("Csls.Debugger.Worker", "csls-debugger-worker.dll")]
    [DataRow("Csls.Debugger.Dump.Worker", "csls-debugger-dump-worker.dll")]
    public void ResolvedAssemblyMatchesRunningTestConfiguration(string projectName, string assemblyFileName)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string path = projectName switch
        {
            "Csls.App" => EditorToolResolver.ResolveLauncher(repositoryRoot),
            "Csls.Worker" => EditorToolResolver.ResolveServerWorker(repositoryRoot),
            _ => EditorToolResolver.ResolveBuiltAssembly(repositoryRoot, projectName, assemblyFileName)
        };
        AssemblyConfigurationAttribute? configuration = typeof(EditorToolResolverTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>();
        Assert.IsNotNull(configuration);
        AssertAssemblyConfiguration(path, configuration.Configuration);
    }

    /// <summary>
    /// Uses the Debug fixture independently from the product workers' build configuration.
    /// </summary>
    [TestMethod]
    public void DebuggingFixtureUsesDebugConfiguration()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        AssertAssemblyConfiguration(EditorToolResolver.ResolveTestProcessHost(repositoryRoot), "Debug");
    }

    private static void AssertAssemblyConfiguration(string path, string expectedConfiguration)
    {
        using FileStream stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        MetadataReader metadata = image.GetMetadataReader();
        CustomAttribute attribute = Assert.ContainsSingle(metadata.GetAssemblyDefinition().GetCustomAttributes()
            .Select(metadata.GetCustomAttribute)
            .Where(candidate => IsConfigurationAttribute(metadata, candidate)));
        BlobReader value = metadata.GetBlobReader(attribute.Value);
        Assert.AreEqual((ushort)1, value.ReadUInt16(), path);
        Assert.AreEqual(expectedConfiguration, value.ReadSerializedString(), path);
        Assert.AreEqual((ushort)0, value.ReadUInt16(), path);
        Assert.AreEqual(0, value.RemainingBytes, path);
    }

    private static bool IsConfigurationAttribute(MetadataReader metadata, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }

        MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        return metadata.StringComparer.Equals(type.Namespace, "System.Reflection") &&
            metadata.StringComparer.Equals(type.Name, nameof(AssemblyConfigurationAttribute));
    }
}
