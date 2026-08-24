using Csls.Client;
using Csls.Control;
using Csls.Control.Contracts;
using StreamJsonRpc;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Csls.Cli.Worker;

/// <summary>
/// Diagnoses a workspace through the selected SDK and a real transient csls session.
/// </summary>
internal static class DoctorCommandHost
{
    /// <summary>
    /// Executes one normalized workspace doctor request.
    /// </summary>
    /// <param name="arguments">The normalized doctor request arguments.</param>
    /// <param name="writeJson">Whether to write a machine-readable response.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Zero when required capabilities pass; otherwise one.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        bool writeJson,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 4)
        {
            CliOutputWriter.WriteError(
                "invalid-request",
                "The launcher supplied an invalid doctor request.",
                writeJson);
            return 1;
        }

        string workspacePath = Path.GetFullPath(arguments[1]);
        string? binlogPath = string.IsNullOrWhiteSpace(arguments[2])
            ? null
            : Path.GetFullPath(arguments[2]);
        string dotNetHost = ResolveDotNetHost();
        var checks = new List<DoctorCheck>();
        ControlDashboardSnapshot? snapshot = null;
        string? sdkVersion = null;
        bool targetExists = Directory.Exists(workspacePath) || File.Exists(workspacePath);
        checks.Add(new DoctorCheck
        {
            Name = "workspace-target",
            Status = targetExists ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
            Message = targetExists
                ? $"Found {workspacePath}."
                : $"The workspace path does not exist: {workspacePath}"
        });

        string workingDirectory = GetWorkingDirectory(workspacePath);
        bool sdkResolved = false;
        if (targetExists)
        {
            try
            {
                ExternalProcessResult sdk = await ExternalProcessRunner.RunAsync(
                    dotNetHost,
                    ["--version"],
                    workingDirectory,
                    cancellationToken).ConfigureAwait(false);
                sdkVersion = sdk.StandardOutput.Trim();
                sdkResolved = sdk.ExitCode == 0 && !string.IsNullOrWhiteSpace(sdkVersion);
                checks.Add(new DoctorCheck
                {
                    Name = "dotnet-sdk",
                    Status = sdkResolved ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
                    Message = sdkResolved
                        ? $"Selected .NET SDK {sdkVersion}."
                        : GetProcessFailureMessage("The .NET SDK could not be selected", sdk)
                });
            }
            catch (Exception exception) when (IsExpectedProcessFailure(exception))
            {
                checks.Add(new DoctorCheck
                {
                    Name = "dotnet-sdk",
                    Status = DoctorCheckStatus.Fail,
                    Message = $"The .NET SDK could not be selected: {exception.Message}"
                });
            }
        }

        if (targetExists && sdkResolved)
        {
            try
            {
                TransientLanguageServerSession transient =
                    await TransientLanguageServerSession.StartAsync(
                        workspacePath,
                        "csls-doctor",
                        cancellationToken).ConfigureAwait(false);
                await using ConfiguredAsyncDisposable transientCleanup =
                    transient.ConfigureAwait(false);
                var client = new ControlRpcClient(
                    ControlEndpoint.GetSocketPath(transient.ProcessId));
                await using ConfiguredAsyncDisposable clientCleanup = client.ConfigureAwait(false);
                snapshot = await client.GetDashboardSnapshotAsync(
                    new ControlDashboardRequest { IncludeDiagnostics = true },
                    cancellationToken).ConfigureAwait(false);
                AddWorkspaceChecks(snapshot, checks);
            }
            catch (Exception exception) when (IsExpectedSessionFailure(exception))
            {
                checks.Add(new DoctorCheck
                {
                    Name = "language-server",
                    Status = DoctorCheckStatus.Fail,
                    Message = $"The transient language server failed: {exception.Message}"
                });
            }
        }

        if (binlogPath is not null)
        {
            await AddBuildCheckAsync(
                workspacePath,
                binlogPath,
                dotNetHost,
                targetExists && sdkResolved,
                checks,
                cancellationToken).ConfigureAwait(false);
        }

        DoctorReport report = CreateReport(
            workspacePath,
            dotNetHost,
            sdkVersion,
            binlogPath,
            snapshot,
            checks);
        CliOutputWriter.WriteDoctor(report, writeJson);
        return report.IsHealthy ? 0 : 1;
    }

    private static async Task AddBuildCheckAsync(
        string workspacePath,
        string binlogPath,
        string dotNetHost,
        bool canBuild,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken)
    {
        if (!canBuild)
        {
            checks.Add(new DoctorCheck
            {
                Name = "msbuild-binlog",
                Status = DoctorCheckStatus.Fail,
                Message = "The requested build was skipped because SDK selection failed."
            });
            return;
        }

        string? binlogDirectory = Path.GetDirectoryName(binlogPath);
        if (!string.IsNullOrWhiteSpace(binlogDirectory))
        {
            Directory.CreateDirectory(binlogDirectory);
        }

        try
        {
            ExternalProcessResult build = await ExternalProcessRunner.RunAsync(
                dotNetHost,
                [
                    "build",
                    workspacePath,
                    $"--binaryLogger:{binlogPath}",
                    "--nologo",
                    "--verbosity:minimal"
                ],
                GetWorkingDirectory(workspacePath),
                cancellationToken).ConfigureAwait(false);
            bool binlogCreated = File.Exists(binlogPath) && new FileInfo(binlogPath).Length > 0;
            bool passed = build.ExitCode == 0 && binlogCreated;
            checks.Add(new DoctorCheck
            {
                Name = "msbuild-binlog",
                Status = passed ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
                Message = passed
                    ? $"Build succeeded and wrote {binlogPath}."
                    : GetProcessFailureMessage(
                        binlogCreated
                            ? $"The build failed; inspect {binlogPath}"
                            : $"The build did not create {binlogPath}",
                        build)
            });
        }
        catch (Exception exception) when (IsExpectedProcessFailure(exception))
        {
            checks.Add(new DoctorCheck
            {
                Name = "msbuild-binlog",
                Status = DoctorCheckStatus.Fail,
                Message = $"The requested build failed to start: {exception.Message}"
            });
        }
    }

    private static void AddWorkspaceChecks(
        ControlDashboardSnapshot snapshot,
        List<DoctorCheck> checks)
    {
        checks.Add(new DoctorCheck
        {
            Name = "language-server",
            Status = DoctorCheckStatus.Pass,
            Message = $"Initialized csls process {snapshot.Session.ProcessId}."
        });
        checks.Add(new DoctorCheck
        {
            Name = "workspace-load",
            Status = snapshot.Workspaces.Count > 0
                ? DoctorCheckStatus.Pass
                : DoctorCheckStatus.Fail,
            Message = snapshot.Workspaces.Count > 0
                ? $"Loaded {snapshot.Workspaces.Count} workspace folder(s)."
                : "The language server did not load a workspace folder."
        });
        checks.Add(new DoctorCheck
        {
            Name = "project-load",
            Status = snapshot.Projects.Count > 0
                ? DoctorCheckStatus.Pass
                : DoctorCheckStatus.Fail,
            Message = snapshot.Projects.Count > 0
                ? $"Loaded {snapshot.Projects.Count} Roslyn project(s) and " +
                    $"{snapshot.Documents.Count} source document(s)."
                : "Roslyn did not load a project from the workspace."
        });

        int errorCount = snapshot.Diagnostics.Count(static item =>
            string.Equals(item.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        int warningCount = snapshot.Diagnostics.Count(static item =>
            string.Equals(item.Severity, "Warning", StringComparison.OrdinalIgnoreCase));
        checks.Add(new DoctorCheck
        {
            Name = "source-diagnostics",
            Status = errorCount > 0 || warningCount > 0
                ? DoctorCheckStatus.Warning
                : DoctorCheckStatus.Pass,
            Message = errorCount > 0 || warningCount > 0
                ? $"Roslyn reported {errorCount} error(s) and {warningCount} warning(s)."
                : "Roslyn reported no source errors or warnings."
        });

        IReadOnlyList<ControlLogEntry> errorLogs =
        [
            .. snapshot.Logs.Where(static item =>
                string.Equals(item.Level, "Error", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Level, "Critical", StringComparison.OrdinalIgnoreCase))
        ];
        checks.Add(new DoctorCheck
        {
            Name = "workspace-logs",
            Status = errorLogs.Count == 0 ? DoctorCheckStatus.Pass : DoctorCheckStatus.Fail,
            Message = errorLogs.Count == 0
                ? "Workspace startup completed without error-level logs."
                : $"Workspace startup captured {errorLogs.Count} error-level log(s): " +
                    errorLogs[0].Message
        });
    }

    private static DoctorReport CreateReport(
        string workspacePath,
        string dotNetHost,
        string? sdkVersion,
        string? binlogPath,
        ControlDashboardSnapshot? snapshot,
        IReadOnlyList<DoctorCheck> checks)
    {
        IReadOnlyList<ControlDiagnosticInfo> actionableDiagnostics = snapshot is null
            ? []
            :
            [
                .. snapshot.Diagnostics.Where(static item =>
                    string.Equals(item.Severity, "Error", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Severity, "Warning", StringComparison.OrdinalIgnoreCase))
            ];
        return new DoctorReport
        {
            WorkspacePath = workspacePath,
            DotNetHost = dotNetHost,
            SdkVersion = sdkVersion,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            BinlogPath = binlogPath,
            Workspaces = snapshot?.Workspaces ?? [],
            Projects = snapshot is null
                ? []
                :
                [
                    .. snapshot.Projects.Select(static item => new DoctorProject
                    {
                        Name = item.Name,
                        FilePath = item.FilePath,
                        DocumentCount = item.DocumentCount,
                        AnalyzerReferenceCount = item.AnalyzerReferenceCount
                    })
                ],
            DocumentCount = snapshot?.Documents.Count ?? 0,
            Diagnostics = actionableDiagnostics,
            TotalDiagnostics = actionableDiagnostics.Count,
            DiagnosticsTruncated = snapshot?.DiagnosticsTruncated ?? false,
            BuildHosts = snapshot?.BuildHosts ?? [],
            Logs = snapshot?.Logs ?? [],
            Checks = checks
        };
    }

    private static string GetWorkingDirectory(string workspacePath) =>
        Directory.Exists(workspacePath)
            ? workspacePath
            : Path.GetDirectoryName(workspacePath) ?? Environment.CurrentDirectory;

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }

    private static bool IsExpectedProcessFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or Win32Exception;

    private static bool IsExpectedSessionFailure(Exception exception) =>
        IsExpectedProcessFailure(exception) ||
        exception is InvalidDataException or SocketException or RemoteInvocationException;

    private static string GetProcessFailureMessage(
        string prefix,
        ExternalProcessResult result)
    {
        string output = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        string normalized = string.Join(
            ' ',
            output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > 512)
        {
            normalized = normalized[..512] + "...";
        }

        string truncation = result.OutputTruncated ? " Captured output was truncated." : string.Empty;
        return string.IsNullOrEmpty(normalized)
            ? $"{prefix} with exit code {result.ExitCode}.{truncation}"
            : $"{prefix} with exit code {result.ExitCode}: {normalized}{truncation}";
    }
}
