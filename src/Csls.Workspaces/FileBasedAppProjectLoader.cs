using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Csls.Workspaces;

/// <summary>
/// Opens SDK-evaluated file-based apps through the Roslyn MSBuild workspace.
/// </summary>
internal static class FileBasedAppProjectLoader
{
    private const int MaximumApiOutputCharacters = 4 * 1024 * 1024;
    private const int MaximumErrorOutputCharacters = 32 * 1024;
    private const int ReadBufferCharacters = 4 * 1024;
    private const int LockRetryMilliseconds = 50;

    /// <summary>
    /// Restores, evaluates, and opens one file-based app without retaining a physical project file.
    /// </summary>
    /// <param name="workspace">The Roslyn workspace that will own the evaluated project.</param>
    /// <param name="entryPointPath">The absolute file-based app entry point.</param>
    /// <param name="reportDiagnostic">The workspace diagnostic reporter.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The opened Roslyn project.</returns>
    internal static async Task<Project> OpenProjectAsync(
        MSBuildWorkspace workspace,
        string entryPointPath,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPointPath);
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        entryPointPath = Path.GetFullPath(entryPointPath);

        using FileStream projectLock = await AcquireProjectLockAsync(
            entryPointPath,
            cancellationToken).ConfigureAwait(false);
        await DotNetWorkspaceRestorer
            .RestoreAsync([entryPointPath], cancellationToken)
            .ConfigureAwait(false);
        (string projectPath, string content) = await EvaluateAsync(
            entryPointPath,
            reportDiagnostic,
            cancellationToken).ConfigureAwait(false);
        projectPath = CreateMaterializedProjectPath(entryPointPath, projectPath);

        bool materialized = false;
        try
        {
            using (var stream = new FileStream(
                projectPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                materialized = true;
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(content.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Project project = await workspace.OpenProjectAsync(
                projectPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Solution solution = project.Solution.WithProjectFilePath(project.Id, entryPointPath);
            return solution.GetProject(project.Id)
                ?? throw new InvalidOperationException(
                    $"The file-based app project disappeared after loading {entryPointPath}.");
        }
        finally
        {
            if (materialized)
            {
                try
                {
                    File.Delete(projectPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    reportDiagnostic(
                        WorkspaceDiagnosticKind.Warning,
                        $"Could not remove temporary file-based app project {projectPath}: " +
                        exception.Message);
                }
            }
        }
    }

    private static async Task<(string ProjectPath, string Content)> EvaluateAsync(
        string entryPointPath,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic,
        CancellationToken cancellationToken)
    {
        string? configuredDotNetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(configuredDotNetHost)
                ? "dotnet"
                : configuredDotNetHost,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(entryPointPath)
                ?? throw new InvalidDataException(
                    $"File-based app entry point has no parent: {entryPointPath}")
        };
        startInfo.ArgumentList.Add("run-api");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The .NET file-based app evaluator did not start.");
        Task<string> standardOutput = ReadBoundedAsync(
            process.StandardOutput,
            MaximumApiOutputCharacters,
            "file-based app evaluator output");
        Task<string> standardError = ReadBoundedAsync(
            process.StandardError,
            MaximumErrorOutputCharacters,
            "file-based app evaluator error output");
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);
        try
        {
            string request = CreateRequest(entryPointPath);
            await process.StandardInput
                .WriteLineAsync(request.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (TryKill(process))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            }

            throw;
        }

        string output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet run-api failed for {entryPointPath} with exit code " +
                $"{process.ExitCode}: {error.Trim()}");
        }

        using var response = JsonDocument.Parse(output);
        JsonElement root = response.RootElement;
        string responseType = root.GetProperty("$type").GetString()
            ?? throw new InvalidDataException("The .NET file-based app evaluator omitted its result type.");
        int version = root.GetProperty("Version").GetInt32();
        if (version != 1)
        {
            throw new InvalidDataException(
                $"The .NET file-based app evaluator returned unsupported version {version}.");
        }

        if (responseType.Equals("Error", StringComparison.Ordinal))
        {
            string message = root.GetProperty("Message").GetString()
                ?? "The .NET file-based app evaluator failed.";
            string details = root.GetProperty("Details").GetString() ?? string.Empty;
            throw new InvalidDataException($"{message}{Environment.NewLine}{details}".Trim());
        }

        if (!responseType.Equals("Project", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The .NET file-based app evaluator returned unexpected result {responseType}.");
        }

        ReportDiagnostics(root, reportDiagnostic);
        string projectPath = root.GetProperty("ProjectPath").GetString()
            ?? throw new InvalidDataException(
                "The .NET file-based app evaluator omitted its project path.");
        string expectedProjectPath = entryPointPath + ".csproj";
        if (!string.Equals(
            Path.GetFullPath(projectPath),
            Path.GetFullPath(expectedProjectPath),
            PathComparison))
        {
            throw new InvalidDataException(
                $"The .NET file-based app evaluator returned unexpected project path {projectPath}.");
        }

        string content = root.GetProperty("Content").GetString()
            ?? throw new InvalidDataException(
                "The .NET file-based app evaluator omitted its project content.");
        return (projectPath, content);
    }

    private static async Task<FileStream> AcquireProjectLockAsync(
        string entryPointPath,
        CancellationToken cancellationToken)
    {
        string lockDirectory = CreateStateDirectory("file-app-locks");
        string lockName = CreateEntryPointIdentity(entryPointPath);
        string lockPath = Path.Join(lockDirectory, lockName + ".lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(LockRetryMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static string CreateMaterializedProjectPath(
        string entryPointPath,
        string evaluatedProjectPath)
    {
        string projectDirectory = Path.Join(
            CreateStateDirectory("file-app-projects"),
            CreateEntryPointIdentity(entryPointPath));
        Directory.CreateDirectory(projectDirectory);
        return Path.Join(projectDirectory, Path.GetFileName(evaluatedProjectPath));
    }

    private static string CreateStateDirectory(string name)
    {
        string localDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localDataPath))
        {
            localDataPath = Path.GetTempPath();
        }

        string directory = Path.Join(localDataPath, "csls", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateEntryPointIdentity(string entryPointPath)
    {
        string normalizedPath = OperatingSystem.IsWindows()
            ? Path.GetFullPath(entryPointPath).ToUpperInvariant()
            : Path.GetFullPath(entryPointPath);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
    }

    private static void ReportDiagnostics(
        JsonElement root,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
        foreach (JsonElement diagnostic in root.GetProperty("Diagnostics").EnumerateArray())
        {
            string message = diagnostic.GetProperty("Message").GetString()
                ?? "Invalid file-based app directive.";
            JsonElement location = diagnostic.GetProperty("Location");
            string path = location.GetProperty("Path").GetString() ?? string.Empty;
            int line = location
                .GetProperty("Span")
                .GetProperty("Start")
                .GetProperty("Line")
                .GetInt32() + 1;
            reportDiagnostic(WorkspaceDiagnosticKind.Failure, $"{path}({line}): {message}");
        }
    }

    private static string CreateRequest(string entryPointPath)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("$type", "GetProject");
            writer.WriteString("EntryPointFileFullPath", entryPointPath);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        string description)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacters);
        try
        {
            var retained = new StringBuilder(Math.Min(maximumCharacters, ReadBufferCharacters));
            bool exceededLimit = false;
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                int remaining = maximumCharacters - retained.Length;
                if (remaining > 0)
                {
                    retained.Append(buffer, 0, Math.Min(read, remaining));
                }

                exceededLimit |= read > remaining;
            }

            if (exceededLimit)
            {
                throw new InvalidDataException(
                    $"The {description} exceeded {maximumCharacters} characters.");
            }

            return retained.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
