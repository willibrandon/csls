using ModelContextProtocol;

namespace Csls.Mcp.Worker;

/// <summary>
/// Validates debugger activation inputs before a worker process is started.
/// </summary>
internal static class McpDebuggerLaunchValidator
{
    private const int MaximumPathLength = 4096;

    /// <summary>
    /// Validates managed launch paths and an optional initial breakpoint.
    /// </summary>
    /// <param name="program">The managed executable or assembly.</param>
    /// <param name="workingDirectory">The target working directory.</param>
    /// <param name="runtimeHostPath">The optional managed runtime host.</param>
    /// <param name="initialSourcePath">The optional initial breakpoint source.</param>
    /// <param name="initialLine">The optional one-based initial breakpoint line.</param>
    internal static void ValidateLaunch(
        string program,
        string workingDirectory,
        string? runtimeHostPath,
        string? initialSourcePath,
        int? initialLine)
    {
        ValidateExistingFile(program, nameof(program));
        ValidateExistingDirectory(workingDirectory, nameof(workingDirectory));
        if (runtimeHostPath is not null)
        {
            ValidateExistingFile(runtimeHostPath, nameof(runtimeHostPath));
        }

        if ((initialSourcePath is null) != (initialLine is null))
        {
            throw new McpException(
                "debugger_request_invalid: initialSourcePath and initialLine must be specified together.");
        }

        if (initialSourcePath is not null)
        {
            ValidateExistingFile(initialSourcePath, nameof(initialSourcePath));
            if (initialLine <= 0)
            {
                throw new McpException(
                    "debugger_request_invalid: initialLine must be a positive one-based line number.");
            }
        }
    }

    /// <summary>
    /// Validates a process identifier selected for attachment.
    /// </summary>
    /// <param name="processId">The selected operating-system process identifier.</param>
    internal static void ValidateAttach(int processId)
    {
        if (processId <= 0)
        {
            throw new McpException(
                "debugger_request_invalid: processId must be a positive process identifier.");
        }
    }

    private static void ValidateExistingFile(string path, string name)
    {
        ValidateAbsolutePath(path, name);
        if (!File.Exists(path))
        {
            throw new McpException(
                $"debugger_request_invalid: {name} does not name an existing file: {path}");
        }
    }

    private static void ValidateExistingDirectory(string path, string name)
    {
        ValidateAbsolutePath(path, name);
        if (!Directory.Exists(path))
        {
            throw new McpException(
                $"debugger_request_invalid: {name} does not name an existing directory: {path}");
        }
    }

    private static void ValidateAbsolutePath(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumPathLength ||
            !Path.IsPathFullyQualified(path))
        {
            throw new McpException(
                $"debugger_request_invalid: {name} must be an absolute path containing " +
                $"between 1 and {MaximumPathLength} characters.");
        }
    }
}
