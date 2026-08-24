using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using LspCodeAction = Csls.Protocol.CodeAction;
using LspRange = Csls.Protocol.Range;

namespace Csls.Benchmarks;

/// <summary>
/// Measures verified semantic code actions against an immutable C# project snapshot.
/// </summary>
[BenchmarkCategory("CodeActions")]
[MemoryDiagnoser]
public class CodeActionBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private CodeActionParams _parameters = null!;
    private CodeActionParams _implementInterfaceParameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real SDK project and verifies the missing-using benchmark fixture.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-code-action-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Program.cs");
        string implementInterfacePath = Path.Join(_fixturePath, "ImplementInterface.cs");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(documentPath, DocumentText).ConfigureAwait(false);
        await File.WriteAllTextAsync(implementInterfacePath, ImplementInterfaceText)
            .ConfigureAwait(false);

        _workspaceManager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        await _workspaceManager.LoadAsync([_fixturePath], CancellationToken.None)
            .ConfigureAwait(false);
        _parameters = new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Range = new LspRange(new Position(6, 26), new Position(6, 39)),
            Context = new CodeActionContext
            {
                Diagnostics = [],
                Only = ["quickfix"]
            }
        };
        _implementInterfaceParameters = new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(implementInterfacePath)
            },
            Range = new LspRange(new Position(7, 29), new Position(7, 36)),
            Context = new CodeActionContext
            {
                Diagnostics = [],
                Only = ["quickfix"]
            }
        };

        IReadOnlyList<LspCodeAction> actions = await _workspaceManager.GetCodeActionsAsync(
            _parameters,
            CancellationToken.None).ConfigureAwait(false);
        LspCodeAction action = actions.Count == 1
            ? actions[0]
            : throw new InvalidOperationException(
                "The missing-using benchmark fixture produced an unexpected action count.");
        if (action.Title != "Add using System.Text" ||
            action.Edit is not { DocumentChanges.Count: > 0 })
        {
            throw new InvalidOperationException(
                "The missing-using benchmark fixture produced no verified edit.");
        }

        IReadOnlyList<LspCodeAction> implementations =
            await _workspaceManager.GetCodeActionsAsync(
                _implementInterfaceParameters,
                CancellationToken.None).ConfigureAwait(false);
        LspCodeAction implementation = implementations.Count == 1
            ? implementations[0]
            : throw new InvalidOperationException(
                "The implement-interface benchmark fixture produced an unexpected action count.");
        if (implementation.Title != "Implement interface" ||
            implementation.Edit is not { DocumentChanges.Count: > 0 })
        {
            throw new InvalidOperationException(
                "The implement-interface benchmark fixture produced no verified edit.");
        }
    }

    /// <summary>
    /// Measures repeated candidate discovery, import insertion, validation, and edit conversion.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<LspCodeAction>> AddMissingUsingAsync() =>
        _workspaceManager.GetCodeActionsAsync(_parameters, CancellationToken.None);

    /// <summary>
    /// Measures required-member discovery, generation, validation, and edit conversion.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<LspCodeAction>> ImplementInterfaceAsync() =>
        _workspaceManager.GetCodeActionsAsync(
            _implementInterfaceParameters,
            CancellationToken.None);

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
            <ImplicitUsings>disable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static string Build()
            {
                var builder = new StringBuilder();
                return builder.ToString();
            }
        }
        """;

    private const string ImplementInterfaceText = """
        namespace InterfaceActions;

        public interface IRunner
        {
            string Run(int value);
        }

        public sealed class Runner : IRunner
        {
        }
        """;
}
