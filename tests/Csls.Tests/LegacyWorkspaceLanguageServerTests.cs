using Csls.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies old-style .NET Framework workspaces through the real production server.
/// </summary>
[TestClass]
public sealed class LegacyWorkspaceLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Loads an old-style .NET Framework project and verifies semantic language features.
    /// </summary>
    [TestMethod]
    public async Task LegacyNetFrameworkProjectProvidesSemanticLanguageFeatures()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-legacy-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string baseTypePath = Path.Join(fixturePath, "LegacyBase.cs");
            string derivedTypePath = Path.Join(fixturePath, "LegacyDerived.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "LegacyFixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                baseTypePath,
                BaseTypeText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                derivedTypePath,
                DerivedTypeText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await RestoreAsync(fixturePath, TestContext.CancellationToken).ConfigureAwait(false);
            string launchPath = Path.Join(fixturePath, "unrelated-launch-directory");
            Directory.CreateDirectory(launchPath);
            await File.WriteAllTextAsync(
                Path.Join(launchPath, "global.json"),
                UnavailableSdkText,
                TestContext.CancellationToken).ConfigureAwait(false);

            string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
            string workerPath = Path.Join(
                EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                "bin",
                "Csls.Worker",
                "debug",
                "csls-worker.dll");
            Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
            var lsp = LspProcessSession.Start(
                "csls-legacy-workspace-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                launchPath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(derivedTypePath, DerivedTypeText).ConfigureAwait(false);

            JsonElement hoverElement = await lsp.RequestHoverAsync(
                derivedTypePath,
                new Position(4, 36),
                TestContext.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The legacy source returned no hover.");
            Hover hover = hoverElement.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException("The legacy source returned invalid hover.");
            Assert.Contains("string LegacyBase.Name", hover.Contents.Value);

            Location definition = Assert.ContainsSingle(await lsp.RequestDefinitionsAsync(
                derivedTypePath,
                new Position(4, 36),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(DocumentUri.FromFileSystemPath(baseTypePath), definition.Uri);
            Assert.AreEqual(new Position(4, 30), definition.Range.Start);

            DocumentDiagnosticReport diagnostics = await lsp.RequestDiagnosticsAsync(
                derivedTypePath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", diagnostics.Kind);
            Assert.IsNotNull(diagnostics.Items);
            Assert.IsEmpty(diagnostics.Items);

            string shutdownDiagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                shutdownDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Loads framework references from the platform legacy build host without a NuGet fallback.
    /// </summary>
    [TestMethod]
    [TestCategory("LegacyBuildHost")]
    public async Task PlatformLegacyBuildHostProvidesFrameworkReferences()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CSLS_REQUIRE_LEGACY_BUILD_HOST"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive(
                "Set CSLS_REQUIRE_LEGACY_BUILD_HOST=true after provisioning the platform host.");
        }

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-platform-legacy-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "LegacyWindow.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "PlatformLegacyFixture.csproj"),
                PlatformProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                PlatformDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
            string workerPath = Path.Join(
                EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                "bin",
                "Csls.Worker",
                "debug",
                "csls-worker.dll");
            Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
            var lsp = LspProcessSession.Start(
                "csls-platform-legacy-workspace-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, PlatformDocumentText)
                .ConfigureAwait(false);

            JsonElement? hoverElement = await lsp.RequestHoverAsync(
                documentPath,
                new Position(6, 37),
                TestContext.CancellationToken).ConfigureAwait(false);
            if (hoverElement is null)
            {
                CSharpDebugInfo debugInfo = await lsp.RequestDebugInfoAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                string failureDiagnostics = await lsp.ShutdownAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                throw new InvalidDataException(
                    "The platform framework reference returned no hover. " +
                    $"Workspace phase: {debugInfo.Workspace.Phase}; " +
                    $"folders: {debugInfo.Workspace.Folders.Count}." +
                    Environment.NewLine +
                    failureDiagnostics);
            }

            Hover hover = hoverElement.Value.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException(
                    "The platform framework reference returned invalid hover.");
            Assert.Contains("string Form.Text { get; set; }", hover.Contents.Value);

            DocumentDiagnosticReport diagnostics = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", diagnostics.Kind);
            Assert.IsNotNull(diagnostics.Items);
            Assert.IsEmpty(diagnostics.Items);

            string shutdownDiagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                shutdownDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static async Task RestoreAsync(
        string fixturePath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = fixturePath
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add("LegacyFixture.csproj");
        startInfo.ArgumentList.Add("--nologo");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The legacy fixture restore did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Legacy restore failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    private const string ProjectText = """
        <Project ToolsVersion="Current" DefaultTargets="Build"
                 xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
            <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
            <ProjectGuid>{2417CAAE-84A5-453D-A35D-E1CBD28B1880}</ProjectGuid>
            <OutputType>Library</OutputType>
            <RootNamespace>LegacyFixture</RootNamespace>
            <AssemblyName>LegacyFixture</AssemblyName>
            <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
            <LangVersion>latest</LangVersion>
            <RestoreProjectStyle>PackageReference</RestoreProjectStyle>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="LegacyBase.cs" />
            <Compile Include="LegacyDerived.cs" />
          </ItemGroup>
          <ItemGroup>
            <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies"
                              Version="1.0.3"
                              PrivateAssets="All" />
          </ItemGroup>
          <Import Project="$(MSBuildToolsPath)/Microsoft.CSharp.targets" />
        </Project>
        """;

    private const string BaseTypeText = """
        namespace LegacyFixture
        {
            public class LegacyBase
            {
                public virtual string Name => "legacy";
            }
        }
        """;

    private const string DerivedTypeText = """
        namespace LegacyFixture
        {
            public sealed class LegacyDerived : LegacyBase
            {
                public string Read() => Name;
            }
        }
        """;

    private const string UnavailableSdkText = """
        {
          "sdk": {
            "version": "99.0.100",
            "rollForward": "disable"
          }
        }
        """;

    private const string PlatformProjectText = """
        <Project ToolsVersion="Current" DefaultTargets="Build"
                 xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
            <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
            <ProjectGuid>{9CE946A0-9926-43A9-A0AB-D00A31EC11EC}</ProjectGuid>
            <OutputType>Library</OutputType>
            <RootNamespace>PlatformLegacyFixture</RootNamespace>
            <AssemblyName>PlatformLegacyFixture</AssemblyName>
            <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
            <LangVersion>latest</LangVersion>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="LegacyWindow.cs" />
            <Reference Include="System" />
            <Reference Include="System.Core" />
            <Reference Include="System.Windows.Forms" />
          </ItemGroup>
          <Import Project="$(MSBuildToolsPath)/Microsoft.CSharp.targets" />
        </Project>
        """;

    private const string PlatformDocumentText = """
        using System.Windows.Forms;

        namespace PlatformLegacyFixture
        {
            public sealed class LegacyWindow : Form
            {
                public string ReadTitle() => Text;
            }
        }
        """;
}
