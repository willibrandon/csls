namespace Csls.Workspaces;

/// <summary>
/// Discovers all solution or project entry points beneath one bounded workspace root.
/// </summary>
internal static class WorkspaceDiscovery
{
    private const int MaximumDirectories = 10_000;
    private const int MaximumWorkspaceFiles = 1_000;
    private static readonly EnumerationOptions s_enumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };
    private static readonly HashSet<string> s_excludedDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".direnv",
        ".dotnet",
        ".git",
        ".hg",
        ".nuget",
        ".svn",
        "bin",
        "node_modules",
        "obj"
    };

    /// <summary>
    /// Finds explicit solution or project files while excluding generated dependency trees.
    /// </summary>
    /// <param name="rootPath">An existing solution, project, source file, or directory.</param>
    /// <returns>All preferred workspace entry points in deterministic path order.</returns>
    internal static IReadOnlyList<string> Discover(string rootPath)
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

            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            throw new InvalidDataException(
                $"Workspace file {fullPath} must be a .slnx, .sln, .csproj, or .cs file.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Workspace root does not exist: {fullPath}");
        }

        var solutions = new List<string>();
        var projects = new List<string>();
        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(fullPath);
        int visitedDirectories = 0;
        while (pendingDirectories.TryDequeue(out string? directoryPath))
        {
            visitedDirectories++;
            if (visitedDirectories > MaximumDirectories)
            {
                throw new InvalidDataException(
                    $"Workspace discovery exceeded {MaximumDirectories} directories beneath {fullPath}.");
            }

            foreach (string filePath in Directory.EnumerateFiles(
                directoryPath,
                "*",
                s_enumerationOptions))
            {
                string extension = Path.GetExtension(filePath);
                if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    AddWorkspaceFile(
                        solutions,
                        filePath,
                        fullPath,
                        solutions.Count + projects.Count);
                }
                else if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    AddWorkspaceFile(
                        projects,
                        filePath,
                        fullPath,
                        solutions.Count + projects.Count);
                }
            }

            foreach (string childPath in Directory.EnumerateDirectories(
                directoryPath,
                "*",
                s_enumerationOptions))
            {
                var child = new DirectoryInfo(childPath);
                if (!s_excludedDirectoryNames.Contains(child.Name))
                {
                    pendingDirectories.Enqueue(child.FullName);
                }
            }
        }

        List<string> selected = solutions.Count > 0 ? solutions : projects;
        selected.Sort(StringComparer.Ordinal);
        return selected;
    }

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
}
