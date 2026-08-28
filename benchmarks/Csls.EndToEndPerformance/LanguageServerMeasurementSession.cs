using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Measures one published csls launcher through its production LSP streams.
/// </summary>
internal sealed class LanguageServerMeasurementSession : IAsyncDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_resourceSettleTime = TimeSpan.FromMilliseconds(250);
    private readonly long _startedTimestamp;
    private readonly Process _process;
    private readonly Task<string> _standardErrorTask;
    private readonly SystemTextJsonFormatter _formatter;
    private readonly HeaderDelimitedMessageHandler _messageHandler;
    private readonly JsonRpc _rpc;

    private LanguageServerMeasurementSession(
        long startedTimestamp,
        Process process,
        Task<string> standardErrorTask,
        SystemTextJsonFormatter formatter,
        HeaderDelimitedMessageHandler messageHandler,
        JsonRpc rpc)
    {
        _startedTimestamp = startedTimestamp;
        _process = process;
        _standardErrorTask = standardErrorTask;
        _formatter = formatter;
        _messageHandler = messageHandler;
        _rpc = rpc;
    }

    /// <summary>
    /// Starts the published Native AOT launcher and connects its production LSP streams.
    /// </summary>
    /// <param name="serverPath">The absolute published csls launcher path.</param>
    /// <param name="workspacePath">The absolute measured workspace path.</param>
    /// <returns>The connected measurement session.</returns>
    internal static LanguageServerMeasurementSession Start(
        string serverPath,
        string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath
        };
        startInfo.ArgumentList.Add("lsp");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        long startedTimestamp = Stopwatch.GetTimestamp();
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The published csls process did not start.");
        try
        {
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = LspRpcJson.CreateSerializerOptions()
            };
            var messageHandler = new HeaderDelimitedMessageHandler(
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                formatter);
            var rpc = new JsonRpc(messageHandler)
            {
                CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
                DisplayName = "csls-end-to-end-performance"
            };
            rpc.StartListening();
            return new LanguageServerMeasurementSession(
                startedTimestamp,
                process,
                standardErrorTask,
                formatter,
                messageHandler,
                rpc);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Initializes, loads, samples, and cleanly shuts down one real workspace session.
    /// </summary>
    /// <param name="iteration">The one-based process iteration number.</param>
    /// <param name="options">The validated measurement configuration.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The complete end-to-end performance measurement.</returns>
    internal async Task<PerformanceMeasurement> MeasureAsync(
        int iteration,
        PerformanceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        string workspacePath = options.WorkspacePath;
        CSharpDebugInfo uninitialized = await RequestDebugInfoAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
            uninitialized.Workspace.Phase,
            "Uninitialized",
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The new language server reported phase {uninitialized.Workspace.Phase}.");
        }

        TimeSpan startupDuration = Stopwatch.GetElapsedTime(_startedTimestamp);
        long workspaceStartedTimestamp = Stopwatch.GetTimestamp();
        using var capabilities = JsonDocument.Parse("{}");
        JsonElement initializationResult = await _rpc
            .InvokeWithParameterObjectAsync<JsonElement>(
                "initialize",
                new InitializeParams
                {
                    ProcessId = Environment.ProcessId,
                    ClientInfo = new ClientInfo { Name = "Csls.EndToEndPerformance" },
                    RootUri = DocumentUri.FromFileSystemPath(workspacePath),
                    WorkspaceFolders =
                    [
                        new WorkspaceFolder
                        {
                            Uri = DocumentUri.FromFileSystemPath(workspacePath),
                            Name = Path.GetFileName(workspacePath)
                        }
                    ],
                    Capabilities = capabilities.RootElement
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (initializationResult.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The language server returned an invalid initialize result.");
        }

        await _rpc.NotifyWithParameterObjectAsync(
            "initialized",
            new InitializedParams()).ConfigureAwait(false);
        CSharpDebugInfo debugInfo = await WaitUntilReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        TimeSpan workspaceDuration = Stopwatch.GetElapsedTime(workspaceStartedTimestamp);
        TimeSpan readyDuration = Stopwatch.GetElapsedTime(_startedTimestamp);
        await Task.Delay(s_resourceSettleTime, cancellationToken).ConfigureAwait(false);
        ProcessTreeSnapshot processTree = await ProcessTreeReader.CaptureAsync(
            _process.Id,
            cancellationToken).ConfigureAwait(false);
        int projectCount = debugInfo.Workspace.Folders.Sum(static folder => folder.ProjectCount);
        int documentCount = debugInfo.Workspace.Folders.Sum(static folder => folder.DocumentCount);
        if (projectCount == 0 || documentCount == 0)
        {
            throw new InvalidDataException(
                "The measured workspace became ready without Roslyn projects and source documents.");
        }

        var operations = new PerformanceOperationRecorder();
        ControlSessionInfo controlSession = await WaitForControlSessionAsync(
            workspacePath,
            cancellationToken).ConfigureAwait(false);
        var control = new ControlRpcClient(controlSession.SocketPath);
        await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
        ControlDashboardSnapshot workspaceSnapshot = await operations.MeasureAsync(
            "control/workspace",
            () => control.GetDashboardSnapshotAsync(
                new ControlDashboardRequest { IncludeDiagnostics = false },
                cancellationToken)).ConfigureAwait(false);
        PerformanceDocument document = await SelectDocumentAsync(
            workspaceSnapshot,
            workspacePath,
            cancellationToken).ConfigureAwait(false);
        ControlProjectInfo project = workspaceSnapshot.Projects
            .Where(item => string.Equals(
                item.Name,
                document.ProjectName,
                StringComparison.Ordinal))
            .MaxBy(GetProjectRank)
            ?? throw new InvalidDataException(
                $"The probe project was not found: {document.ProjectName}");
        ProcessTreeSnapshot resourcesBefore = await ProcessTreeReader.CaptureAsync(
            _process.Id,
            cancellationToken).ConfigureAwait(false);
        long operationsStartedTimestamp = Stopwatch.GetTimestamp();

        await operations.MeasureAsync(
            "lsp/open-document",
            () => OpenDocumentAsync(document)).ConfigureAwait(false);
        DocumentDiagnosticReport diagnostics = await operations.MeasureAsync(
            "lsp/diagnostics",
            () => RequestDiagnosticsAsync(document.Path, cancellationToken))
            .ConfigureAwait(false);
        _ = await operations.MeasureAsync(
            "lsp/hover",
            () => RequestHoverAsync(document, cancellationToken)).ConfigureAwait(false);
        _ = await operations.MeasureAsync(
            "lsp/completion",
            () => RequestCompletionAsync(document, cancellationToken)).ConfigureAwait(false);
        _ = await operations.MeasureAsync(
            "lsp/code-actions",
            () => RequestCodeActionsAsync(document, diagnostics, cancellationToken))
            .ConfigureAwait(false);
        _ = await operations.MeasureAsync(
            "lsp/formatting",
            () => RequestFormattingAsync(document, cancellationToken)).ConfigureAwait(false);
        await operations.MeasureAsync(
            "lsp/edit-diagnostics",
            () => ChangeAndDiagnoseAsync(document, cancellationToken)).ConfigureAwait(false);
        ControlDashboardSnapshot analyzedSnapshot = await operations.MeasureAsync(
            "control/analyzers-generators",
            () => control.GetDashboardSnapshotAsync(
                new ControlDashboardRequest
                {
                    IncludeDiagnostics = true,
                    DiagnosticsProjectId = project.Id
                },
                cancellationToken)).ConfigureAwait(false);
        if (!analyzedSnapshot.DiagnosticsLoaded || project.AnalyzerReferenceCount == 0)
        {
            throw new InvalidDataException(
                "The measured project did not execute its configured analyzers and generators.");
        }

        await operations.MeasureAsync(
            "mcp/session",
            () => McpMeasurementClient.MeasureAsync(
                options.McpServerPath,
                controlSession.ProcessId,
                workspacePath,
                cancellationToken)).ConfigureAwait(false);
        await operations.MeasureAsync(
            "dashboard/attach",
            () => DashboardMeasurementClient.MeasureAsync(
                options.ServerPath,
                controlSession.ProcessId,
                workspacePath,
                cancellationToken)).ConfigureAwait(false);
        await Task.Delay(s_resourceSettleTime, cancellationToken).ConfigureAwait(false);
        ProcessTreeSnapshot resourcesAfter = await ProcessTreeReader.CaptureAsync(
            _process.Id,
            cancellationToken).ConfigureAwait(false);
        TimeSpan liveOperationsDuration = Stopwatch.GetElapsedTime(operationsStartedTimestamp);
        double processorTimeMilliseconds = Math.Max(
            0,
            TimeSpan.FromTicks(
                resourcesAfter.ProcessorTimeTicks - resourcesBefore.ProcessorTimeTicks)
                .TotalMilliseconds);
        double processorUtilizationPercent = liveOperationsDuration > TimeSpan.Zero
            ? processorTimeMilliseconds /
                liveOperationsDuration.TotalMilliseconds /
                Environment.ProcessorCount *
                100
            : 0;

        await operations.MeasureAsync(
            "lsp/shutdown",
            () => ShutdownAsync(cancellationToken)).ConfigureAwait(false);
        await operations.MeasureAsync(
            "cli/transient",
            () => CliMeasurementClient.MeasureTransientAsync(
                options.ServerPath,
                workspacePath,
                cancellationToken)).ConfigureAwait(false);
        return new PerformanceMeasurement
        {
            Iteration = iteration,
            CacheState = iteration == 1 ? "cold" : "warm",
            ProbeDocumentPath = document.Path,
            StartupMilliseconds = startupDuration.TotalMilliseconds,
            WorkspaceLoadMilliseconds = workspaceDuration.TotalMilliseconds,
            ReadyMilliseconds = readyDuration.TotalMilliseconds,
            Operations = operations.Operations,
            ProjectCount = projectCount,
            DocumentCount = documentCount,
            AnalyzerReferenceCount = project.AnalyzerReferenceCount,
            AnalyzerNames =
            [
                .. project.AnalyzerPaths
                    .Select(static path => Path.GetFileName(path))
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
            ],
            ProcessCount = Math.Max(
                processTree.ProcessCount,
                Math.Max(resourcesBefore.ProcessCount, resourcesAfter.ProcessCount)),
            WorkingSetBytes = Math.Max(
                processTree.WorkingSetBytes,
                Math.Max(resourcesBefore.WorkingSetBytes, resourcesAfter.WorkingSetBytes)),
            PrivateMemoryBytes = Math.Max(
                processTree.PrivateMemoryBytes,
                Math.Max(
                    resourcesBefore.PrivateMemoryBytes,
                    resourcesAfter.PrivateMemoryBytes)),
            ProcessorTimeMilliseconds = processorTimeMilliseconds,
            ProcessorUtilizationPercent = processorUtilizationPercent
        };
    }

    private async Task OpenDocumentAsync(PerformanceDocument document)
    {
        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(document.Path),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = document.Text
                }
            }).ConfigureAwait(false);
    }

    private Task<DocumentDiagnosticReport> RequestDiagnosticsAsync(
        string documentPath,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
            "textDocument/diagnostic",
            new DocumentDiagnosticParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Identifier = "csls"
            },
            cancellationToken);

    private Task<JsonElement?> RequestHoverAsync(
        PerformanceDocument document,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
            "textDocument/hover",
            CreatePositionParams(document),
            cancellationToken);

    private Task<CompletionList> RequestCompletionAsync(
        PerformanceDocument document,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CompletionList>(
            "textDocument/completion",
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(document.Path)
                },
                Position = document.Position,
                Context = new CompletionContext
                {
                    TriggerKind = CompletionTriggerKind.Invoked
                }
            },
            cancellationToken);

    private Task<IReadOnlyList<CodeAction>> RequestCodeActionsAsync(
        PerformanceDocument document,
        DocumentDiagnosticReport diagnostics,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<CodeAction>>(
            "textDocument/codeAction",
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(document.Path)
                },
                Range = new LspRange(document.Position, document.Position),
                Context = new CodeActionContext
                {
                    Diagnostics = diagnostics.Items ?? []
                }
            },
            cancellationToken);

    private Task<IReadOnlyList<TextEdit>> RequestFormattingAsync(
        PerformanceDocument document,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<TextEdit>>(
            "textDocument/formatting",
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(document.Path)
                },
                Options = new FormattingOptions
                {
                    TabSize = 4,
                    InsertSpaces = true
                }
            },
            cancellationToken);

    private async Task ChangeAndDiagnoseAsync(
        PerformanceDocument document,
        CancellationToken cancellationToken)
    {
        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didChange",
            new DidChangeTextDocumentParams
            {
                TextDocument = new VersionedTextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(document.Path),
                    Version = 2
                },
                ContentChanges =
                [
                    new TextDocumentContentChangeEvent
                    {
                        Text = document.Text + Environment.NewLine
                    }
                ]
            }).ConfigureAwait(false);
        _ = await RequestDiagnosticsAsync(document.Path, cancellationToken)
            .ConfigureAwait(false);
    }

    private static TextDocumentPositionParams CreatePositionParams(
        PerformanceDocument document) => new()
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(document.Path)
            },
            Position = document.Position
        };

    private static async Task<PerformanceDocument> SelectDocumentAsync(
        ControlDashboardSnapshot snapshot,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var projectRanks = snapshot.Projects
            .GroupBy(static project => project.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Max(GetProjectRank),
                StringComparer.Ordinal);
        ControlDocumentInfo[] candidates =
        [
            .. snapshot.Documents
                .Where(document => document.FilePath is not null &&
                    IsWorkspaceSourcePath(document.FilePath, workspacePath) &&
                    string.Equals(
                        Path.GetExtension(document.FilePath),
                        ".cs",
                        StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(document.FilePath))
                .OrderByDescending(document => projectRanks.GetValueOrDefault(
                    document.ProjectName))
                .ThenBy(static document => document.FilePath, StringComparer.Ordinal)
        ];
        foreach (ControlDocumentInfo candidate in candidates)
        {
            string path = candidate.FilePath!;
            string text = await File.ReadAllTextAsync(path, cancellationToken)
                .ConfigureAwait(false);
            int offset = FindSemanticIdentifierOffset(text);
            if (offset >= 0)
            {
                return new PerformanceDocument
                {
                    Path = path,
                    Text = text,
                    Position = GetPosition(text, offset),
                    ProjectName = candidate.ProjectName
                };
            }
        }

        throw new InvalidDataException(
            "The measured workspace has no readable C# document with a semantic identifier.");
    }

    private static int GetProjectRank(ControlProjectInfo project)
    {
        return project.AnalyzerPaths.Any(static path => string.Equals(
            Path.GetFileName(path),
            "Csls.SourceGen.dll",
            StringComparison.OrdinalIgnoreCase))
            ? 2
            : project.AnalyzerReferenceCount > 0
                ? 1
                : 0;
    }

    private static bool IsWorkspaceSourcePath(string path, string workspacePath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string relativePath = Path.GetRelativePath(workspacePath, path);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", comparison) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", comparison))
        {
            return false;
        }

        string[] excludedSegments = [".git", "artifacts", "bin", "node_modules", "obj"];
        return !relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => excludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static int FindSemanticIdentifierOffset(string text)
    {
        string[] markers = ["class ", "record ", "interface ", "enum ", "struct ", "namespace "];
        foreach (string marker in markers)
        {
            int markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
            {
                int offset = markerIndex + marker.Length;
                while (offset < text.Length && char.IsWhiteSpace(text[offset]))
                {
                    offset++;
                }

                if (offset < text.Length &&
                    (char.IsLetter(text[offset]) || text[offset] == '_'))
                {
                    return offset;
                }
            }
        }

        for (int offset = 0; offset < text.Length; offset++)
        {
            if (char.IsLetter(text[offset]) || text[offset] == '_')
            {
                return offset;
            }
        }

        return -1;
    }

    private static Position GetPosition(string text, int offset)
    {
        int line = 0;
        int lineStart = 0;
        for (int index = 0; index < offset; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        return new Position(line, offset - lineStart);
    }

    /// <summary>
    /// Releases the RPC transport and terminates an unfinished process tree.
    /// </summary>
    /// <returns>A task that completes after process cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }

    private async Task<CSharpDebugInfo> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_pollInterval);
        while (true)
        {
            CSharpDebugInfo result = await RequestDebugInfoAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(result.Workspace.Phase, "Ready", StringComparison.Ordinal))
            {
                return result;
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new UnreachableException();
            }
        }
    }

    private async Task<ControlSessionInfo> WaitForControlSessionAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_pollInterval);
        while (true)
        {
            ProcessTreeSnapshot processTree = await ProcessTreeReader.CaptureAsync(
                _process.Id,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ControlSessionInfo> sessions = await ControlSessionDiscovery
                .DiscoverAsync(cancellationToken).ConfigureAwait(false);
            ControlSessionInfo? match = sessions.FirstOrDefault(session =>
                processTree.ProcessIds.Contains(session.ProcessId) &&
                session.WorkspaceRoots.Any(root => WorkspaceContains(root, workspacePath)));
            if (match is not null)
            {
                return match;
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new UnreachableException();
            }
        }
    }

    private static bool WorkspaceContains(string root, string workspacePath)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string fullRoot = Path.GetFullPath(root);
        string fullWorkspacePath = Path.GetFullPath(workspacePath);
        if (string.Equals(fullRoot, fullWorkspacePath, comparison))
        {
            return true;
        }

        string relativePath = Path.GetRelativePath(fullRoot, fullWorkspacePath);
        return !Path.IsPathRooted(relativePath) &&
            !relativePath.Equals("..", comparison) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", comparison);
    }

    private Task<CSharpDebugInfo> RequestDebugInfoAsync(CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CSharpDebugInfo>(
            "$/csharp/debugInfo",
            new InitializedParams(),
            cancellationToken);

    private async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        object? shutdownResult = await _rpc.InvokeWithParameterObjectAsync<object?>(
            "shutdown",
            new InitializedParams(),
            cancellationToken).ConfigureAwait(false);
        if (shutdownResult is not null)
        {
            throw new InvalidDataException("The LSP shutdown response must be null.");
        }

        await _rpc.NotifyWithParameterObjectAsync(
            "exit",
            new InitializedParams()).ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardError = new ValueTask<string>(_standardErrorTask);
        string diagnostics = await standardError.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The language server exited with code {_process.ExitCode}: {diagnostics}");
        }

        if (diagnostics.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The language server reported an unhandled exception: {diagnostics}");
        }
    }
}
