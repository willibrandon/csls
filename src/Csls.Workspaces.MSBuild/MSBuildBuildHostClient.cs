using Microsoft.CodeAnalysis;
using StreamJsonRpc;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Workspaces;

/// <summary>
/// Loads projects through an isolated csls worker that owns in-process MSBuild.
/// </summary>
internal sealed class MSBuildBuildHostClient
{
    private static readonly TimeSpan s_workingDirectoryReleaseRetryDelay =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_workingDirectoryReleaseTimeout =
        TimeSpan.FromSeconds(10);
    private readonly Action<WorkspaceDiagnosticKind, string> _reportDiagnostic;
    private readonly Dictionary<string, string> _globalProperties;

    /// <summary>
    /// Initializes an isolated build-host client.
    /// </summary>
    /// <param name="globalProperties">The complete MSBuild global property set.</param>
    /// <param name="reportDiagnostic">The workspace diagnostic destination.</param>
    internal MSBuildBuildHostClient(
        IReadOnlyDictionary<string, string> globalProperties,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(globalProperties);
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        _globalProperties = new Dictionary<string, string>(
            globalProperties,
            StringComparer.OrdinalIgnoreCase);
        _reportDiagnostic = reportDiagnostic;
    }

    /// <summary>
    /// Loads every requested project without exposing the caller process to MSBuild state.
    /// </summary>
    /// <param name="projectPaths">The absolute project paths in solution order.</param>
    /// <param name="cancellationToken">The load cancellation token.</param>
    /// <returns>The completed project states in project and target-framework order.</returns>
    internal async Task<IReadOnlyList<MSBuildProjectSnapshot>> LoadAsync(
        IReadOnlyList<string> projectPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        if (projectPaths.Count == 0)
        {
            return [];
        }

        DirectoryInfo workingDirectory = Directory.CreateTempSubdirectory(
            "csls-msbuild-build-host-");
        try
        {
            return await LoadInBuildHostAsync(
                projectPaths,
                workingDirectory.FullName,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DeleteWorkingDirectoryAsync(workingDirectory.FullName).ConfigureAwait(false);
        }
    }

    private static async Task DeleteWorkingDirectoryAsync(string path)
    {
        long startedTimestamp = Stopwatch.GetTimestamp();
        while (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception)
                when (OperatingSystem.IsWindows() &&
                    exception is IOException or UnauthorizedAccessException &&
                    Stopwatch.GetElapsedTime(startedTimestamp) <
                        s_workingDirectoryReleaseTimeout)
            {
                await Task.Delay(s_workingDirectoryReleaseRetryDelay).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<MSBuildProjectSnapshot>> LoadInBuildHostAsync(
        IReadOnlyList<string> projectPaths,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        string buildHostPath = ResolveBuildHostPath();
        bool isManagedAssembly = string.Equals(
            Path.GetExtension(buildHostPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedAssembly ? ResolveDotNetHost() : buildHostPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (isManagedAssembly)
        {
            startInfo.ArgumentList.Add(buildHostPath);
        }

        startInfo.ArgumentList.Add("--msbuild-build-host");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The MSBuild build host did not start.");
        var processTree = WindowsProcessTreeLifetime.Attach(process);
        await using ConfiguredAsyncDisposable processTreeCleanup =
            processTree.ConfigureAwait(false);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(
            CancellationToken.None);
        MSBuildBuildHostResponse response;
        using (var formatter = new SystemTextJsonFormatter())
        using (var messageHandler = new HeaderDelimitedMessageHandler(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            formatter))
        using (var rpc = new JsonRpc(messageHandler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "csls-msbuild-build-host-client"
        })
        {
            rpc.StartListening();
            try
            {
                response = await rpc
                    .InvokeWithParameterObjectAsync<MSBuildBuildHostResponse>(
                        "msbuild/load",
                        new MSBuildBuildHostRequest(
                            [.. projectPaths],
                            _globalProperties),
                        cancellationToken)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await processTree.TerminateDescendantsAsync().ConfigureAwait(false);
                _ = await standardErrorTask.ConfigureAwait(false);
                throw;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await processTree.TerminateDescendantsAsync().ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The MSBuild build host exited with code {process.ExitCode}: " +
                standardError.Trim());
        }

        foreach (MSBuildBuildHostDiagnostic diagnostic in response.Diagnostics)
        {
            _reportDiagnostic(diagnostic.Kind, diagnostic.Message);
        }

        return response.Snapshots;
    }

    private static string ResolveBuildHostPath()
    {
        string executableName = OperatingSystem.IsWindows()
            ? "csls-worker.exe"
            : "csls-worker";
        const string AssemblyName = "csls-worker.dll";
        string? processPath = Environment.ProcessPath;
        if (processPath is not null && string.Equals(
            Path.GetFileName(processPath),
            executableName,
            StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string localCandidate = Path.Join(AppContext.BaseDirectory, executableName);
        if (IsFrameworkDependentBuildHost(localCandidate))
        {
            return localCandidate;
        }

        string localAssembly = Path.Join(AppContext.BaseDirectory, AssemblyName);
        if (File.Exists(localAssembly))
        {
            return localAssembly;
        }

        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = outputDirectory.Name;
        DirectoryInfo? artifactsBinDirectory = outputDirectory.Parent?.Parent;
        if (artifactsBinDirectory is not null)
        {
            string repositoryCandidate = Path.Join(
                artifactsBinDirectory.FullName,
                "Csls.Worker",
                configuration,
                executableName);
            if (IsFrameworkDependentBuildHost(repositoryCandidate))
            {
                return repositoryCandidate;
            }

            string repositoryAssembly = Path.Join(
                artifactsBinDirectory.FullName,
                "Csls.Worker",
                configuration,
                AssemblyName);
            if (File.Exists(repositoryAssembly))
            {
                return repositoryAssembly;
            }
        }

        throw new FileNotFoundException(
            "The csls MSBuild build host executable was not found.",
            localCandidate);
    }

    private static bool IsFrameworkDependentBuildHost(string path) =>
        File.Exists(path) && File.Exists(Path.Join(
            Path.GetDirectoryName(path)!,
            "csls-worker.dll"));

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }
}
