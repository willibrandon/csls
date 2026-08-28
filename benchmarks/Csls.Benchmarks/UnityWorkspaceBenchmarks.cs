using BenchmarkDotNet.Attributes;
using Csls.Workspaces;

namespace Csls.Benchmarks;

/// <summary>
/// Measures Unity workspace loading while generated editor state is present.
/// </summary>
[BenchmarkCategory("Workspace", "Unity")]
[MemoryDiagnoser]
public class UnityWorkspaceBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private string _documentPath = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Creates a valid Unity-shaped project and a large generated Library tree.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-unity-benchmark-{Guid.NewGuid():N}");
        string assetsPath = Path.Join(_fixturePath, "Assets", "Scripts");
        string projectSettingsPath = Path.Join(_fixturePath, "ProjectSettings");
        Directory.CreateDirectory(assetsPath);
        Directory.CreateDirectory(projectSettingsPath);
        await File.WriteAllTextAsync(
            Path.Join(projectSettingsPath, "ProjectVersion.txt"),
            "m_EditorVersion: 6000.0.0f1\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Assembly-CSharp.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Benchmark.slnx"),
            "<Solution><Project Path=\"Assembly-CSharp.csproj\" /></Solution>")
            .ConfigureAwait(false);
        _documentPath = Path.Join(assetsPath, "BenchmarkBehaviour.cs");
        await File.WriteAllTextAsync(_documentPath, DocumentText).ConfigureAwait(false);

        string packageCachePath = Path.Join(_fixturePath, "Library", "PackageCache");
        for (int index = 0; index < 512; index++)
        {
            string generatedPath = Path.Join(
                packageCachePath,
                $"com.fixture.package{index}",
                "Solution");
            Directory.CreateDirectory(generatedPath);
            await File.WriteAllTextAsync(
                Path.Join(generatedPath, $"Generated{index}.slnx"),
                "<invalid>").ConfigureAwait(false);
        }

        _workspaceManager = BenchmarkWorkspaceManagerFactory.Create();
        await _workspaceManager.LoadAsync([_fixturePath], CancellationToken.None)
            .ConfigureAwait(false);
        WorkspaceInspectionSnapshot inspection = await _workspaceManager
            .InspectAsync(
                includeDiagnostics: false,
                diagnosticsProjectId: null,
                CancellationToken.None)
            .ConfigureAwait(false);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string generatedPrefix = Path.Join(_fixturePath, "Library") +
            Path.DirectorySeparatorChar;
        if (inspection.Projects.Count != 1 ||
            !inspection.Documents.Any(document => string.Equals(
                document.FilePath,
                _documentPath,
                pathComparison)) ||
            inspection.Documents.Any(document => document.FilePath?.StartsWith(
                generatedPrefix,
                pathComparison) == true))
        {
            throw new InvalidOperationException(
                $"The Unity benchmark fixture loaded {inspection.Projects.Count} projects " +
                $"and {inspection.Documents.Count} documents.");
        }
    }

    /// <summary>
    /// Measures discovery, MSBuild evaluation, Roslyn loading, and snapshot publication.
    /// </summary>
    [Benchmark]
    public Task LoadUnityWorkspaceAsync() => _workspaceManager.LoadAsync(
        [_fixturePath],
        CancellationToken.None);

    /// <summary>
    /// Disposes the real workspace and removes the isolated Unity fixture.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Releases the Roslyn workspace and its temporary files.
    /// </summary>
    /// <returns>A value task that completes after all resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _workspaceManager.DisposeAsync().ConfigureAwait(false);
        Directory.Delete(_fixturePath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Assets/**/*.cs" />
          </ItemGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Game;

        public sealed class BenchmarkBehaviour
        {
            public int Value => Math.Abs(-1);
        }
        """;
}
