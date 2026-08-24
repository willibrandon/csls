using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Frozen;

namespace Csls.Workspaces;

/// <summary>
/// Discovers solution, project, and file-based app entry points beneath one bounded workspace root.
/// </summary>
internal static class WorkspaceDiscovery
{
    private const int MaximumDirectories = 10_000;
    private const int MaximumWorkspaceFiles = 1_000;
    private static readonly CSharpParseOptions s_fileBasedAppParseOptions =
        CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp14)
            .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);
    private static readonly EnumerationOptions s_enumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };
    private static readonly FrozenSet<string> s_excludedDirectoryNames = new[]
    {
        ".direnv",
        ".dotnet",
        ".git",
        ".hg",
        ".nuget",
        ".svn",
        ".vs",
        "artifacts",
        "bin",
        "node_modules",
        "obj"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> s_unityGeneratedDirectoryNames = new[]
    {
        "Library",
        "Logs",
        "Temp",
        "UserSettings"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds explicit workspaces and file-based apps while excluding generated dependency trees.
    /// </summary>
    /// <param name="rootPath">An existing solution, project, source file, or directory.</param>
    /// <param name="cancellationToken">The discovery cancellation token.</param>
    /// <returns>All preferred workspace entry points in deterministic path order.</returns>
    internal static IReadOnlyList<string> Discover(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string fullPath = Path.GetFullPath(rootPath);
        if (File.Exists(fullPath))
        {
            string extension = Path.GetExtension(fullPath);
            if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return [fullPath];
            }

            if (IsFileBasedApp(fullPath))
            {
                return [fullPath];
            }

            throw new InvalidDataException(
                $"Workspace file {fullPath} must be a solution, project, or file-based app.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Workspace root does not exist: {fullPath}");
        }

        var solutions = new List<string>();
        var projects = new List<string>();
        var fileBasedApps = new List<string>();
        var pendingDirectories = new Queue<(string Path, bool IsProjectCone)>();
        pendingDirectories.Enqueue((fullPath, false));
        int visitedDirectories = 0;
        while (pendingDirectories.TryDequeue(out (string Path, bool IsProjectCone) pending))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directoryPath = pending.Path;
            visitedDirectories++;
            if (visitedDirectories > MaximumDirectories)
            {
                throw new InvalidDataException(
                    $"Workspace discovery exceeded {MaximumDirectories} directories beneath {fullPath}.");
            }

            bool containsProject = false;
            List<string>? fileBasedAppCandidates = null;
            foreach (string filePath in Directory.EnumerateFiles(
                directoryPath,
                "*",
                s_enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(filePath);
                if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    AddWorkspaceFile(
                        solutions,
                        filePath,
                        fullPath,
                        solutions.Count + projects.Count + fileBasedApps.Count);
                }
                else if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    containsProject = true;
                    AddWorkspaceFile(
                        projects,
                        filePath,
                        fullPath,
                        solutions.Count + projects.Count + fileBasedApps.Count);
                }
                else if (!pending.IsProjectCone &&
                    extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
                    IsDiscoverableFileBasedApp(filePath, cancellationToken))
                {
                    (fileBasedAppCandidates ??= []).Add(filePath);
                }
            }

            if (!containsProject && fileBasedAppCandidates is not null)
            {
                foreach (string fileBasedApp in fileBasedAppCandidates)
                {
                    AddWorkspaceFile(
                        fileBasedApps,
                        fileBasedApp,
                        fullPath,
                        solutions.Count + projects.Count + fileBasedApps.Count);
                }
            }

            foreach (string childPath in Directory.EnumerateDirectories(
                directoryPath,
                "*",
                s_enumerationOptions))
            {
                EnqueueIfIncluded(
                    pendingDirectories,
                    childPath,
                    pending.IsProjectCone || containsProject);
            }
        }

        List<string> selected = solutions.Count > 0 ? solutions : projects;
        selected.AddRange(fileBasedApps);
        selected.Sort(StringComparer.Ordinal);
        return selected;
    }

    /// <summary>
    /// Determines whether an existing path is a valid explicit file-based app entry point.
    /// </summary>
    /// <param name="path">The absolute source path to inspect.</param>
    /// <returns>True for C# files and extensionless files beginning with a shebang.</returns>
    internal static bool IsFileBasedApp(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            StartsWithShebang(path);
    }

    /// <summary>
    /// Determines whether a path is beneath a generated or dependency directory excluded from discovery.
    /// </summary>
    /// <param name="rootPath">The workspace root, solution, project, or source file path.</param>
    /// <param name="candidatePath">The document path to inspect.</param>
    /// <returns>True when an excluded directory segment contains the candidate.</returns>
    internal static bool IsExcludedPath(string rootPath, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        string containmentRoot = File.Exists(rootPath)
            ? Path.GetDirectoryName(rootPath) ?? rootPath
            : rootPath;
        string? relativeDirectory = Path.GetDirectoryName(
            Path.GetRelativePath(containmentRoot, candidatePath));
        if (relativeDirectory is null)
        {
            return false;
        }

        string currentDirectory = containmentRoot;
        foreach (string segment in relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (s_excludedDirectoryNames.Contains(segment) ||
                s_unityGeneratedDirectoryNames.Contains(segment) &&
                IsUnityProjectDirectory(currentDirectory))
            {
                return true;
            }

            currentDirectory = Path.Join(currentDirectory, segment);
        }

        return false;
    }

    private static void EnqueueIfIncluded(
        Queue<(string Path, bool IsProjectCone)> pendingDirectories,
        string directoryPath,
        bool isProjectCone)
    {
        string directoryName = Path.GetFileName(directoryPath);
        string? parentDirectory = Path.GetDirectoryName(directoryPath);
        if (!s_excludedDirectoryNames.Contains(directoryName) &&
            !(s_unityGeneratedDirectoryNames.Contains(directoryName) &&
                parentDirectory is not null &&
                IsUnityProjectDirectory(parentDirectory)))
        {
            pendingDirectories.Enqueue((directoryPath, isProjectCone));
        }
    }

    private static bool IsUnityProjectDirectory(string directoryPath) =>
        Directory.Exists(Path.Join(directoryPath, "Assets")) &&
        File.Exists(Path.Join(directoryPath, "ProjectSettings", "ProjectVersion.txt"));

    private static void AddWorkspaceFile(
        List<string> workspaceFiles,
        string workspaceFile,
        string rootPath,
        int discoveredCount)
    {
        if (discoveredCount >= MaximumWorkspaceFiles)
        {
            throw new InvalidDataException(
                $"Workspace discovery exceeded {MaximumWorkspaceFiles} entry points beneath {rootPath}.");
        }

        workspaceFiles.Add(Path.GetFullPath(workspaceFile));
    }

    private static bool StartsWithShebang(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> prefix = stackalloc byte[5];
            int read = stream.Read(prefix);
            return prefix[..read] is [(byte)'#', (byte)'!', ..] or
                [0xEF, 0xBB, 0xBF, (byte)'#', (byte)'!'];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDiscoverableFileBasedApp(
        string path,
        CancellationToken cancellationToken)
    {
        if (StartsWithShebang(path))
        {
            return true;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            var source = SourceText.From(stream, encoding: null);
            CompilationUnitSyntax root = CSharpSyntaxTree
                .ParseText(
                    source,
                    s_fileBasedAppParseOptions,
                    cancellationToken: cancellationToken)
                .GetCompilationUnitRoot(cancellationToken);
            return root.GetLeadingTrivia().Any(
                static trivia => trivia.IsKind(SyntaxKind.IgnoredDirectiveTrivia)) &&
                root.Members.Any(static member => member.IsKind(SyntaxKind.GlobalStatement));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
