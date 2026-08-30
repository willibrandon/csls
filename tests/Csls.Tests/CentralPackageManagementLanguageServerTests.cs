using Csls.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies centrally managed NuGet dependencies through real restore and workspace loading.
/// </summary>
[TestClass]
public sealed class CentralPackageManagementLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Resolves a versionless package reference from Directory.Packages.props.
    /// </summary>
    [TestMethod]
    public async Task CentralPackageVersionProvidesSemanticLanguageFeatures()
    {
        string fixtureRoot = Path.Join(
            Path.GetTempPath(),
            $"csls-central-package-management-{Guid.NewGuid():N}");
        string dependencyPath = Path.Join(fixtureRoot, "dependency");
        string feedPath = Path.Join(fixtureRoot, "feed");
        string workspacePath = Path.Join(fixtureRoot, "workspace");
        Directory.CreateDirectory(dependencyPath);
        Directory.CreateDirectory(feedPath);
        Directory.CreateDirectory(workspacePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(dependencyPath, "Fixture.Dependency.csproj"),
                DependencyProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(dependencyPath, "IWidget.cs"),
                DependencyDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await RunDotNetAsync(
                dependencyPath,
                [
                    "pack",
                    "Fixture.Dependency.csproj",
                    "--output",
                    feedPath,
                    "--nologo",
                    "--disable-build-servers"
                ]).ConfigureAwait(false);

            string documentPath = Path.Join(workspacePath, "WidgetFactory.cs");
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Directory.Packages.props"),
                CentralPackagesText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "NuGet.config"),
                NuGetConfigurationText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "CentralPackageFixture.csproj"),
                WorkspaceProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                WorkspaceDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await RunDotNetAsync(
                workspacePath,
                ["restore", "CentralPackageFixture.csproj", "--nologo"])
                .ConfigureAwait(false);

            string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
            string workerPath = Path.Join(
                EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
                "bin",
                "Csls.Worker",
                "debug",
                "csls-worker.dll");
            Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-central-package-management-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                workspacePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                workspacePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, WorkspaceDocumentText)
                .ConfigureAwait(false);

            JsonElement hoverElement = await lsp.RequestHoverAsync(
                documentPath,
                new Position(6, 12),
                TestContext.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The centrally managed dependency returned no hover.");
            Hover hover = hoverElement.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException(
                    "The centrally managed dependency returned invalid hover.");
            Assert.Contains("interface Fixture.Dependency.IWidget", hover.Contents.Value);

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
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private async Task RunDotNetAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The fixture .NET process did not start.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Fixture .NET command failed.{Environment.NewLine}" +
            $"{standardOutput}{Environment.NewLine}{standardError}");
    }

    private const string DependencyProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <PackageId>Fixture.Dependency</PackageId>
            <Version>1.0.0</Version>
          </PropertyGroup>
        </Project>
        """;

    private const string DependencyDocumentText = """
        namespace Fixture.Dependency;

        public interface IWidget;
        """;

    private const string CentralPackagesText = """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="Fixture.Dependency" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private const string NuGetConfigurationText = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="fixture" value="../feed" />
          </packageSources>
        </configuration>
        """;

    private const string WorkspaceProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Fixture.Dependency" />
          </ItemGroup>
        </Project>
        """;

    private const string WorkspaceDocumentText = """
        using Fixture.Dependency;

        namespace CentralPackageFixture;

        public sealed class WidgetFactory
        {
            public IWidget? Current { get; init; }
        }
        """;
}
