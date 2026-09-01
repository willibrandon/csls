using System.Security;

namespace Csls.Tests;

/// <summary>
/// Creates a real multi-document project backed by the analyzer execution probe.
/// </summary>
internal sealed class AnalyzerExecutionProbeFixture : IAsyncDisposable
{
    private AnalyzerExecutionProbeFixture(
        string rootPath,
        string markerPath,
        string releasePath,
        IReadOnlyList<string> documentPaths,
        IReadOnlyList<string> documentTexts)
    {
        RootPath = rootPath;
        MarkerPath = markerPath;
        ReleasePath = releasePath;
        DocumentPaths = documentPaths;
        DocumentTexts = documentTexts;
    }

    /// <summary>
    /// Gets the isolated real project root.
    /// </summary>
    internal string RootPath { get; }

    /// <summary>
    /// Gets the analyzer lifecycle marker observed across the process boundary.
    /// </summary>
    internal string MarkerPath { get; }

    /// <summary>
    /// Gets the release file that unblocks real analyzer execution.
    /// </summary>
    internal string ReleasePath { get; }

    /// <summary>
    /// Gets the ordered source documents loaded by the language server.
    /// </summary>
    internal IReadOnlyList<string> DocumentPaths { get; }

    /// <summary>
    /// Gets the exact ordered source text written to each fixture document.
    /// </summary>
    internal IReadOnlyList<string> DocumentTexts { get; }

    /// <summary>
    /// Gets the changed first document text used to invalidate the workspace generation.
    /// </summary>
    internal static string UpdatedFirstDocumentText => CreateDocumentText(0, value: 2);

    /// <summary>
    /// Creates and writes one isolated analyzer-backed multi-document project.
    /// </summary>
    /// <param name="repositoryRoot">The current csls repository root.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The initialized real project fixture.</returns>
    internal static async Task<AnalyzerExecutionProbeFixture> CreateAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        string analyzerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.CancellationProbeAnalyzer",
            "debug",
            "Csls.CancellationProbeAnalyzer.dll");
        if (!File.Exists(analyzerPath))
        {
            throw new FileNotFoundException(
                "The compiled analyzer execution probe was not found.",
                analyzerPath);
        }

        string transportPath = Path.Join(
            Path.GetDirectoryName(analyzerPath)
                ?? throw new InvalidOperationException("The analyzer path has no parent directory."),
            "Csls.CancellationProbeTransport.dll");
        if (!File.Exists(transportPath))
        {
            throw new FileNotFoundException(
                "The compiled analyzer execution transport was not found.",
                transportPath);
        }

        string rootPath = Path.Join(
            Path.GetTempPath(),
            $"csls-analyzer-execution-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        string markerPath = Path.Join(rootPath, "AnalyzerExecutionProbe.marker");
        string releasePath = Path.Join(rootPath, "AnalyzerExecutionProbe.release");
        string escapedAnalyzerPath = SecurityElement.Escape(analyzerPath)
            ?? throw new InvalidOperationException("The analyzer path could not be XML escaped.");
        string escapedTransportPath = SecurityElement.Escape(transportPath)
            ?? throw new InvalidOperationException("The transport path could not be XML escaped.");
        string projectText = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <EnableNETAnalyzers>false</EnableNETAnalyzers>
              </PropertyGroup>
              <ItemGroup>
                <Analyzer Include="{{escapedAnalyzerPath}}" />
                <Analyzer Include="{{escapedTransportPath}}" />
                <AdditionalFiles Include="AnalyzerExecutionProbe.marker" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            Path.Join(rootPath, "AnalyzerExecutionFixture.csproj"),
            projectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            markerPath,
            string.Empty,
            cancellationToken).ConfigureAwait(false);

        string[] documentPaths = new string[3];
        string[] documentTexts = new string[3];
        for (int index = 0; index < documentPaths.Length; index++)
        {
            documentPaths[index] = Path.Join(rootPath, $"Document{index}.cs");
            documentTexts[index] = CreateDocumentText(index, value: 1);
            await File.WriteAllTextAsync(
                documentPaths[index],
                documentTexts[index],
                cancellationToken).ConfigureAwait(false);
        }

        return new AnalyzerExecutionProbeFixture(
            rootPath,
            markerPath,
            releasePath,
            documentPaths,
            documentTexts);
    }

    /// <summary>
    /// Creates the cross-process file that releases every active analyzer probe.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the release file is visible.</returns>
    internal Task ReleaseAsync(CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(ReleasePath, "release", cancellationToken);

    /// <summary>
    /// Removes the release file before the next workspace generation is analyzed.
    /// </summary>
    internal void ResetRelease() => File.Delete(ReleasePath);

    /// <summary>
    /// Reads every ordered analyzer lifecycle event through the real marker file.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The ordered lifecycle event lines.</returns>
    internal Task<string[]> ReadEventsAsync(CancellationToken cancellationToken) =>
        File.ReadAllLinesAsync(MarkerPath, cancellationToken);

    /// <summary>
    /// Deletes the isolated fixture after every real process releases it.
    /// </summary>
    /// <returns>A completed value task.</returns>
    public async ValueTask DisposeAsync()
    {
        await DirectoryReleaseWaiter.DeleteAsync(RootPath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static string CreateDocumentText(int index, int value) => $$"""
        namespace AnalyzerExecutionFixture;

        public static class Document{{index}}
        {
            public static int Value => {{value}};
        }
        """;
}
