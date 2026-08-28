using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;

namespace Csls.Benchmarks;

/// <summary>
/// Measures authoritative workspace refresh after a client file operation.
/// </summary>
[BenchmarkCategory("Workspace", "FileOperations")]
[MemoryDiagnoser]
public class WorkspaceFileOperationBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private CreateFilesParams _parameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real SDK project and verifies that a created source joins the workspace.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-operation-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Existing.cs"),
            ExistingText).ConfigureAwait(false);

        _workspaceManager = BenchmarkWorkspaceManagerFactory.Create();
        await _workspaceManager.LoadAsync([_fixturePath], CancellationToken.None)
            .ConfigureAwait(false);

        string createdPath = Path.Join(_fixturePath, "Created.cs");
        await File.WriteAllTextAsync(createdPath, CreatedText).ConfigureAwait(false);
        _parameters = new CreateFilesParams
        {
            Files =
            [
                new FileCreate
                {
                    Uri = DocumentUri.FromFileSystemPath(createdPath)
                }
            ]
        };
        WorkspaceMaintenanceResult? refresh = await _workspaceManager
            .ApplyCreatedFilesAsync(_parameters, CancellationToken.None)
            .ConfigureAwait(false);
        IReadOnlyList<WorkspaceSymbol> symbols = await _workspaceManager
            .GetWorkspaceSymbolsAsync(
                new WorkspaceSymbolParams { Query = "CreatedType" },
                CancellationToken.None)
            .ConfigureAwait(false);
        if (refresh is null || symbols.Count != 1)
        {
            throw new InvalidOperationException(
                "The workspace file-operation benchmark fixture did not refresh.");
        }
    }

    /// <summary>
    /// Measures reloading a real SDK workspace after a source-file creation notification.
    /// </summary>
    [Benchmark]
    public Task<WorkspaceMaintenanceResult?> RefreshCreatedFileAsync() =>
        _workspaceManager.ApplyCreatedFilesAsync(_parameters, CancellationToken.None);

    /// <summary>
    /// Disposes the real workspace and removes the isolated project fixture.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Releases the Roslyn workspace and its temporary project files.
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
          </PropertyGroup>
        </Project>
        """;

    private const string ExistingText = """
        namespace Fixture;

        public sealed class ExistingType;
        """;

    private const string CreatedText = """
        namespace Fixture;

        public sealed class CreatedType;
        """;
}
