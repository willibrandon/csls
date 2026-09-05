namespace Csls.Mcp.Worker;

/// <summary>
/// Validates debugger activation inputs before a worker process is started.
/// </summary>
internal static class McpDebuggerLaunchValidator
{
    private const int MaximumSourceFileMapCount = 256;
    private const int MaximumPathLength = 4096;

    /// <summary>
    /// Validates managed launch paths and an optional initial breakpoint.
    /// </summary>
    /// <param name="program">The managed executable or assembly.</param>
    /// <param name="workingDirectory">The target working directory.</param>
    /// <param name="runtimeHostPath">The optional managed runtime host.</param>
    /// <param name="sourceFileMap">Build-time source prefixes mapped to local source prefixes.</param>
    /// <param name="initialSourcePath">The optional initial breakpoint source.</param>
    /// <param name="initialLine">The optional one-based initial breakpoint line.</param>
    internal static void ValidateLaunch(
        string program,
        string workingDirectory,
        string? runtimeHostPath,
        IReadOnlyDictionary<string, string>? sourceFileMap,
        string? initialSourcePath,
        int? initialLine)
    {
        ValidateExistingFile(program, nameof(program));
        ValidateExistingDirectory(workingDirectory, nameof(workingDirectory));
        if (runtimeHostPath is not null)
        {
            ValidateExistingFile(runtimeHostPath, nameof(runtimeHostPath));
        }

        ValidateSourceFileMap(sourceFileMap);

        if ((initialSourcePath is null) != (initialLine is null))
        {
            throw InvalidRequest(
                "initialSourcePath and initialLine must be specified together.");
        }

        if (initialSourcePath is not null)
        {
            ValidateExistingFile(initialSourcePath, nameof(initialSourcePath));
            if (initialLine <= 0)
            {
                throw InvalidRequest(
                    "initialLine must be a positive one-based line number.");
            }
        }
    }

    /// <summary>
    /// Validates a process identifier selected for attachment.
    /// </summary>
    /// <param name="processId">The selected operating-system process identifier.</param>
    /// <param name="sourceFileMap">Build-time source prefixes mapped to local source prefixes.</param>
    internal static void ValidateAttach(
        int processId,
        IReadOnlyDictionary<string, string>? sourceFileMap)
    {
        if (processId <= 0)
        {
            throw InvalidRequest("processId must be a positive process identifier.");
        }

        ValidateSourceFileMap(sourceFileMap);
    }

    private static void ValidateSourceFileMap(
        IReadOnlyDictionary<string, string>? sourceFileMap)
    {
        if (sourceFileMap is null)
        {
            return;
        }

        if (sourceFileMap.Count > MaximumSourceFileMapCount)
        {
            throw InvalidRequest(
                $"sourceFileMap cannot exceed {MaximumSourceFileMapCount} entries.");
        }

        foreach ((string buildPath, string localPath) in sourceFileMap)
        {
            ValidatePortableAbsolutePath(buildPath, "sourceFileMap key");
            ValidatePortableAbsolutePath(localPath, "sourceFileMap value");
        }
    }

    private static void ValidatePortableAbsolutePath(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumPathLength ||
            path[0] != '/' &&
            !path.StartsWith("\\\\", StringComparison.Ordinal) &&
            !(path.Length >= 3 &&
                char.IsAsciiLetter(path[0]) &&
                path[1] == ':' &&
                path[2] is '/' or '\\'))
        {
            throw InvalidRequest(
                $"{name} must be an absolute POSIX, drive-letter, or UNC path " +
                $"containing between 1 and {MaximumPathLength} characters.");
        }
    }

    private static void ValidateExistingFile(string path, string name)
    {
        ValidateAbsolutePath(path, name);
        if (!File.Exists(path))
        {
            throw InvalidRequest($"{name} does not name an existing file: {path}");
        }
    }

    private static void ValidateExistingDirectory(string path, string name)
    {
        ValidateAbsolutePath(path, name);
        if (!Directory.Exists(path))
        {
            throw InvalidRequest($"{name} does not name an existing directory: {path}");
        }
    }

    private static void ValidateAbsolutePath(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumPathLength ||
            !Path.IsPathFullyQualified(path))
        {
            throw InvalidRequest(
                $"{name} must be an absolute path containing " +
                $"between 1 and {MaximumPathLength} characters.");
        }
    }

    private static McpDebuggerException InvalidRequest(string message) =>
        new("debugger_request_invalid", message);
}
