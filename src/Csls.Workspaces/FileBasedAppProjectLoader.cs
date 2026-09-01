using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Csls.Workspaces;

/// <summary>
/// Evaluates and materializes SDK-backed file-based apps for an isolated project loader.
/// </summary>
internal static class FileBasedAppProjectLoader
{
    private const int MaximumApiResponseCharacters = 4 * 1024 * 1024;
    private const int MaximumErrorOutputCharacters = 32 * 1024;
    private const int ReadBufferCharacters = 4 * 1024;
    private const int LockRetryMilliseconds = 50;

    /// <summary>
    /// Restores, evaluates, and materializes file-based apps for one bounded load operation.
    /// </summary>
    /// <typeparam name="TResult">The result produced by the isolated project loader.</typeparam>
    /// <param name="entryPointPaths">The absolute file-based app entry points.</param>
    /// <param name="logger">The workspace restore logger.</param>
    /// <param name="reportDiagnostic">The workspace diagnostic reporter.</param>
    /// <param name="loadProjectsAsync">The loader invoked while all generated projects exist.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The isolated project-loader result.</returns>
    internal static async Task<TResult> UseProjectsAsync<TResult>(
        IReadOnlyList<string> entryPointPaths,
        ILogger logger,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic,
        Func<
            IReadOnlyList<string>,
            IReadOnlyDictionary<string, string>,
            CancellationToken,
            Task<TResult>> loadProjectsAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entryPointPaths);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        ArgumentNullException.ThrowIfNull(loadProjectsAsync);
        string[] entryPoints =
        [
            .. entryPointPaths
                .Select(Path.GetFullPath)
                .Distinct(PathComparer)
                .Order(PathComparer)
        ];
        var projectFilePaths = new Dictionary<string, string>(PathComparer);
        if (entryPoints.Length == 0)
        {
            return await loadProjectsAsync(
                [],
                projectFilePaths,
                cancellationToken).ConfigureAwait(false);
        }

        var projectLocks = new FileStream?[entryPoints.Length];
        string[] materializedProjectPaths = new string[entryPoints.Length];
        string[] markerPaths = new string[entryPoints.Length];
        bool[] materialized = new bool[entryPoints.Length];
        bool[] markerCreated = new bool[entryPoints.Length];
        try
        {
            for (int index = 0; index < entryPoints.Length; index++)
            {
                string entryPointPath = entryPoints[index];
                projectLocks[index] = await AcquireProjectLockAsync(
                    entryPointPath,
                    cancellationToken).ConfigureAwait(false);
                materializedProjectPaths[index] = GetMaterializedProjectPath(entryPointPath);
                markerPaths[index] = CreateMaterializationMarkerPath(entryPointPath);
                await RecoverInterruptedMaterializationAsync(
                    entryPointPath,
                    materializedProjectPaths[index],
                    markerPaths[index],
                    cancellationToken).ConfigureAwait(false);
            }

            await DotNetWorkspaceRestorer.RestoreAsync(
                entryPoints,
                logger,
                cancellationToken).ConfigureAwait(false);
            (string EvaluatedProjectPath, string Content)[] evaluatedProjects =
                await EvaluateProjectsAsync(
                    entryPoints,
                    reportDiagnostic,
                    cancellationToken).ConfigureAwait(false);
            string[] preparedContents = await Task.WhenAll(
                evaluatedProjects.Select((project, index) =>
                    PrepareMaterializedProjectAsync(
                        project.Content,
                        project.EvaluatedProjectPath,
                        materializedProjectPaths[index],
                        cancellationToken))).ConfigureAwait(false);

            for (int index = 0; index < entryPoints.Length; index++)
            {
                string entryPointPath = entryPoints[index];
                string materializedProjectPath = materializedProjectPaths[index];
                RemoveGeneratedProjectIfPresent(
                    evaluatedProjects[index].EvaluatedProjectPath,
                    entryPointPath);
                RemoveGeneratedProjectIfPresent(materializedProjectPath, entryPointPath);
                await File.WriteAllTextAsync(
                    markerPaths[index],
                    materializedProjectPath,
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);
                markerCreated[index] = true;
                using (var stream = new FileStream(
                    materializedProjectPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    materialized[index] = true;
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await writer.WriteAsync(
                        preparedContents[index].AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                projectFilePaths.Add(materializedProjectPath, entryPointPath);
            }

            return await loadProjectsAsync(
                materializedProjectPaths,
                projectFilePaths,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            for (int index = entryPoints.Length - 1; index >= 0; index--)
            {
                if (materialized[index])
                {
                    try
                    {
                        File.Delete(materializedProjectPaths[index]);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        reportDiagnostic(
                            WorkspaceDiagnosticKind.Warning,
                            $"Could not remove temporary file-based app project " +
                            $"{materializedProjectPaths[index]}: " +
                            exception.Message);
                    }
                }

                if (markerCreated[index])
                {
                    try
                    {
                        File.Delete(markerPaths[index]);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        reportDiagnostic(
                            WorkspaceDiagnosticKind.Warning,
                            $"Could not remove file-based app materialization marker " +
                            $"{markerPaths[index]}: " +
                            exception.Message);
                    }
                }

                if (projectLocks[index] is FileStream projectLock)
                {
                    await projectLock.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Gets the stable private project path used to evaluate one file-based app.
    /// </summary>
    /// <param name="entryPointPath">The absolute file-based app entry point.</param>
    /// <returns>The stable private materialized project path.</returns>
    internal static string GetMaterializedProjectPath(string entryPointPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPointPath);
        string projectDirectory = Path.Join(
            CreateStateDirectory("file-app-projects"),
            CreateEntryPointIdentity(entryPointPath));
        Directory.CreateDirectory(projectDirectory);
        return Path.Join(projectDirectory, Path.GetFileName(entryPointPath) + ".csproj");
    }

    private static async Task<(string EvaluatedProjectPath, string Content)[]>
        EvaluateProjectsAsync(
        string[] entryPointPaths,
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
            WorkingDirectory = Path.GetDirectoryName(entryPointPaths[0])
                ?? throw new InvalidDataException(
                    $"File-based app entry point has no parent: {entryPointPaths[0]}")
        };
        startInfo.ArgumentList.Add("run-api");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The .NET file-based app evaluator did not start.");
        Task<string[]> standardOutput = ReadApiResponsesAsync(
            process.StandardOutput,
            entryPointPaths.Length);
        Task<string> standardError = ReadBoundedAsync(
            process.StandardError,
            MaximumErrorOutputCharacters,
            "file-based app evaluator error output");
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);
        try
        {
            foreach (string request in entryPointPaths.Select(CreateRequest))
            {
                await process.StandardInput
                    .WriteLineAsync(request.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
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

        string[] output = await standardOutput.ConfigureAwait(false);
        string error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet run-api failed with exit code " +
                $"{process.ExitCode}: {error.Trim()}");
        }

        var projects = new (string EvaluatedProjectPath, string Content)[output.Length];
        for (int index = 0; index < output.Length; index++)
        {
            projects[index] = ParseProjectResponse(
                output[index],
                entryPointPaths[index],
                reportDiagnostic);
        }

        return projects;
    }

    private static (string EvaluatedProjectPath, string Content) ParseProjectResponse(
        string output,
        string entryPointPath,
        Action<WorkspaceDiagnosticKind, string> reportDiagnostic)
    {
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

    private static string CreateStateDirectory(string name)
    {
        string localDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localDataPath))
        {
            string userProfilePath = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfilePath))
            {
                throw new InvalidOperationException(
                    "The current user has no local application-data directory.");
            }

            localDataPath = Path.Join(userProfilePath, ".local", "share");
        }

        string directory = Path.Join(localDataPath, "csls", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateMaterializationMarkerPath(string entryPointPath)
    {
        string markerDirectory = CreateStateDirectory("file-app-materializations");
        return Path.Join(
            markerDirectory,
            CreateEntryPointIdentity(entryPointPath) + ".marker");
    }

    private static async Task RecoverInterruptedMaterializationAsync(
        string entryPointPath,
        string materializedProjectPath,
        string markerPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath))
        {
            return;
        }

        string markedProjectPath = (await File.ReadAllTextAsync(
            markerPath,
            cancellationToken).ConfigureAwait(false)).Trim();
        string legacyProjectPath = entryPointPath + ".csproj";
        bool isLegacyProject = string.Equals(
            Path.GetFullPath(markedProjectPath),
            Path.GetFullPath(legacyProjectPath),
            PathComparison);
        bool isPrivateProject = string.Equals(
            Path.GetFullPath(markedProjectPath),
            Path.GetFullPath(materializedProjectPath),
            PathComparison);
        if (!isLegacyProject && !isPrivateProject)
        {
            throw new InvalidDataException(
                $"File-based app materialization marker {markerPath} points to " +
                $"unexpected project {markedProjectPath}.");
        }

        RemoveGeneratedProjectIfPresent(markedProjectPath, entryPointPath);
        File.Delete(markerPath);
    }

    private static void RemoveGeneratedProjectIfPresent(
        string projectPath,
        string entryPointPath)
    {
        if (!File.Exists(projectPath))
        {
            return;
        }

        if (!FileBasedAppProjectArtifact.IsGeneratedForEntryPoint(
            projectPath,
            entryPointPath))
        {
            throw new InvalidOperationException(
                $"Cannot load file-based app {entryPointPath} because {projectPath} " +
                "already exists and is not its generated project.");
        }

        File.Delete(projectPath);
    }

    private static async Task<string> PrepareMaterializedProjectAsync(
        string content,
        string evaluatedProjectPath,
        string materializedProjectPath,
        CancellationToken cancellationToken)
    {
        var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        XElement root = document.Root
            ?? throw new InvalidDataException(
                "The .NET file-based app evaluator returned an empty project.");
        string sourceDirectory = Path.GetDirectoryName(evaluatedProjectPath)
            ?? throw new InvalidDataException(
                $"The generated file-based app project has no parent: {evaluatedProjectPath}");

        foreach (XAttribute include in root
            .Descendants()
            .Where(static element => element.Name.LocalName.Equals(
                "ProjectReference",
                StringComparison.Ordinal))
            .Select(static element => element.Attribute("Include"))
            .OfType<XAttribute>())
        {
            if (string.IsNullOrWhiteSpace(include.Value) ||
                Path.IsPathFullyQualified(include.Value) ||
                include.Value.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            include.Value = Path.GetFullPath(include.Value, sourceDirectory);
        }

        XNamespace projectNamespace = root.Name.Namespace;
        var directoryPaths = new XElement(projectNamespace + "PropertyGroup");
        AddPathPropertyIfPresent(
            directoryPaths,
            projectNamespace,
            "DirectoryBuildPropsPath",
            FindNearestFile(sourceDirectory, "Directory.Build.props"));
        AddPathPropertyIfPresent(
            directoryPaths,
            projectNamespace,
            "DirectoryBuildTargetsPath",
            FindNearestFile(sourceDirectory, "Directory.Build.targets"));
        AddPathPropertyIfPresent(
            directoryPaths,
            projectNamespace,
            "DirectoryPackagesPropsPath",
            FindNearestFile(sourceDirectory, "Directory.Packages.props"));
        string? projectAssetsPath = await MaterializeProjectAssetsAsync(
            root,
            sourceDirectory,
            materializedProjectPath,
            cancellationToken).ConfigureAwait(false);
        AddPathPropertyIfPresent(
            directoryPaths,
            projectNamespace,
            "ProjectAssetsFile",
            projectAssetsPath);
        if (directoryPaths.HasElements)
        {
            root.AddFirst(directoryPaths);
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static async Task<string?> MaterializeProjectAssetsAsync(
        XElement project,
        string sourceDirectory,
        string materializedProjectPath,
        CancellationToken cancellationToken)
    {
        string? artifactsPath = project
            .Descendants()
            .FirstOrDefault(static element => element.Name.LocalName.Equals(
                "ArtifactsPath",
                StringComparison.Ordinal))
            ?.Value;
        if (string.IsNullOrWhiteSpace(artifactsPath))
        {
            return null;
        }

        string evaluatedAssetsPath = Path.Join(
            Path.GetFullPath(artifactsPath, sourceDirectory),
            "obj",
            "project.assets.json");
        if (!File.Exists(evaluatedAssetsPath))
        {
            return null;
        }

        JsonObject assets = JsonNode.Parse(
            await File.ReadAllTextAsync(evaluatedAssetsPath, cancellationToken)
                .ConfigureAwait(false))?.AsObject()
            ?? throw new InvalidDataException(
                $"The file-based app assets file is empty: {evaluatedAssetsPath}");
        if (assets["libraries"] is JsonObject libraries)
        {
            foreach ((string _, JsonNode? value) in libraries)
            {
                if (value is not JsonObject library ||
                    !string.Equals(
                        library["type"]?.GetValue<string>(),
                        "project",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                RebaseProjectAssetPath(library, "path", sourceDirectory);
                RebaseProjectAssetPath(library, "msbuildProject", sourceDirectory);
            }
        }

        string materializedDirectory = Path.GetDirectoryName(materializedProjectPath)
            ?? throw new InvalidDataException(
                $"The materialized file-based app project has no parent: " +
                materializedProjectPath);
        string materializedAssetsPath = Path.Join(
            materializedDirectory,
            "obj",
            "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(materializedAssetsPath)!);
        await File.WriteAllTextAsync(
            materializedAssetsPath,
            assets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        return materializedAssetsPath;
    }

    private static void RebaseProjectAssetPath(
        JsonObject library,
        string propertyName,
        string sourceDirectory)
    {
        string? value = library[propertyName]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(value) && !Path.IsPathFullyQualified(value))
        {
            library[propertyName] = Path.GetFullPath(value, sourceDirectory);
        }
    }

    private static void AddPathPropertyIfPresent(
        XElement propertyGroup,
        XNamespace projectNamespace,
        string propertyName,
        string? path)
    {
        if (path is not null)
        {
            propertyGroup.Add(new XElement(projectNamespace + propertyName, path));
        }
    }

    private static string? FindNearestFile(string startDirectory, string fileName)
    {
        for (var directory = new DirectoryInfo(startDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            string candidate = Path.Join(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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

    private static async Task<string[]> ReadApiResponsesAsync(
        StreamReader reader,
        int expectedResponseCount)
    {
        string[] responses = new string[expectedResponseCount];
        for (int index = 0; index < responses.Length; index++)
        {
            string response = await reader.ReadLineAsync(CancellationToken.None)
                .ConfigureAwait(false) ??
                throw new InvalidDataException(
                    $"The .NET file-based app evaluator returned {index} of " +
                    $"{expectedResponseCount} responses.");

            if (response.Length > MaximumApiResponseCharacters)
            {
                throw new InvalidDataException(
                    $"A file-based app evaluator response exceeded " +
                    $"{MaximumApiResponseCharacters} characters.");
            }

            responses[index] = response;
        }

        string? extraResponse;
        while ((extraResponse = await reader.ReadLineAsync(CancellationToken.None)
            .ConfigureAwait(false)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(extraResponse))
            {
                throw new InvalidDataException(
                    "The .NET file-based app evaluator returned more responses than requested.");
            }
        }

        return responses;
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

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
