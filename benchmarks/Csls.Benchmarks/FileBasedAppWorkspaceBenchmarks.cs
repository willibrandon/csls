using BenchmarkDotNet.Attributes;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csls.Benchmarks;

/// <summary>
/// Measures complete SDK-backed file-based app workspace loading.
/// </summary>
[BenchmarkCategory("Workspace", "FileBasedApps")]
[MemoryDiagnoser]
public class FileBasedAppWorkspaceBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private string _entryPointPath = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Creates and validates one real file-based app through the pinned SDK.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-app-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        File.Copy(
            Path.Join(FindRepositoryRoot(), "global.json"),
            Path.Join(_fixturePath, "global.json"));
        _entryPointPath = Path.Join(_fixturePath, "Benchmark.cs");
        await File.WriteAllTextAsync(_entryPointPath, FileBasedAppText).ConfigureAwait(false);
        _workspaceManager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        await _workspaceManager.LoadAsync([_entryPointPath], CancellationToken.None)
            .ConfigureAwait(false);
        WorkspaceInspectionSnapshot inspection = await _workspaceManager
            .InspectAsync(includeDiagnostics: true, CancellationToken.None)
            .ConfigureAwait(false);
        if (inspection.Projects.Count != 1 ||
            !inspection.Documents.Any(document => string.Equals(
                document.FilePath,
                _entryPointPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)) ||
            inspection.TotalDiagnostics != 0 ||
            File.Exists(_entryPointPath + ".csproj"))
        {
            throw new InvalidOperationException(
                $"The file-based app benchmark fixture loaded " +
                $"{inspection.Projects.Count} projects, {inspection.Documents.Count} documents, " +
                $"{inspection.TotalDiagnostics} diagnostics, and temporary project presence " +
                $"{File.Exists(_entryPointPath + ".csproj")}.");
        }
    }

    /// <summary>
    /// Measures restore, SDK evaluation, Roslyn loading, publication, and prior-workspace disposal.
    /// </summary>
    [Benchmark]
    public Task LoadFileBasedAppAsync() => _workspaceManager.LoadAsync(
        [_entryPointPath],
        CancellationToken.None);

    /// <summary>
    /// Disposes the real workspace and removes the isolated file-based app fixture.
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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The csls repository root was not found.");
    }

    private const string FileBasedAppText = """
        #!/usr/bin/env dotnet
        #:property TargetFramework=net10.0
        Console.WriteLine("benchmark");
        """;
}
