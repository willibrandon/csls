using System.Security;

namespace Csls.Tests;

/// <summary>
/// Creates a real project that loads the compiled Roslyn cancellation probe analyzer.
/// </summary>
internal sealed class CancellationProbeFixture : IAsyncDisposable
{
    private const string DocumentTextValue = """
        namespace CancellationFixture;

        public static class Program
        {
            public static void Main()
            {
            }
        }
        """;

    private CancellationProbeFixture(
        string rootPath,
        string documentPath,
        string markerPath)
    {
        RootPath = rootPath;
        DocumentPath = documentPath;
        MarkerPath = markerPath;
    }

    /// <summary>
    /// Gets the isolated real project root.
    /// </summary>
    internal string RootPath { get; }

    /// <summary>
    /// Gets the source document loaded by the language server.
    /// </summary>
    internal string DocumentPath { get; }

    /// <summary>
    /// Gets the analyzer marker file observed through a real file boundary.
    /// </summary>
    internal string MarkerPath { get; }

    /// <summary>
    /// Gets the exact source text written to the fixture document.
    /// </summary>
    internal static string DocumentText => DocumentTextValue;

    /// <summary>
    /// Creates and writes one isolated analyzer-backed project.
    /// </summary>
    /// <param name="repositoryRoot">The current csls repository root.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The initialized real project fixture.</returns>
    internal static async Task<CancellationProbeFixture> CreateAsync(
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
                "The compiled cancellation probe analyzer was not found.",
                analyzerPath);
        }

        string transportPath = Path.Join(
            Path.GetDirectoryName(analyzerPath)
                ?? throw new InvalidOperationException("The analyzer path has no parent directory."),
            "Csls.CancellationProbeTransport.dll");
        if (!File.Exists(transportPath))
        {
            throw new FileNotFoundException(
                "The compiled cancellation probe transport was not found.",
                transportPath);
        }

        string rootPath = Path.Join(
            Path.GetTempPath(),
            $"csls-cancellation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        string documentPath = Path.Join(rootPath, "Program.cs");
        string markerPath = Path.Join(rootPath, "CancellationProbe.marker");
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
                <AdditionalFiles Include="CancellationProbe.marker" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            Path.Join(rootPath, "CancellationFixture.csproj"),
            projectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            DocumentTextValue,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            markerPath,
            string.Empty,
            cancellationToken).ConfigureAwait(false);
        return new CancellationProbeFixture(rootPath, documentPath, markerPath);
    }

    /// <summary>
    /// Deletes the isolated fixture after every real process releases it.
    /// </summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        Directory.Delete(RootPath, recursive: true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
