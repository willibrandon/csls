namespace Csls.Debugger.Terminal;

/// <summary>
/// Owns one exact temporary directory used by an interactive debugger session.
/// </summary>
internal sealed class DebuggerTerminalEndpoint : IDisposable
{
    private const string DirectoryPrefix = "csls-debugger-terminal-";
    private readonly string _temporaryRoot;
    private int _disposed;

    private DebuggerTerminalEndpoint(string temporaryRoot, string directoryPath)
    {
        _temporaryRoot = temporaryRoot;
        DirectoryPath = directoryPath;
    }

    /// <summary>
    /// Gets the exact session directory.
    /// </summary>
    internal string DirectoryPath { get; }

    /// <summary>
    /// Gets the debugger control socket path.
    /// </summary>
    internal string SocketPath => Path.Join(DirectoryPath, "control.sock");

    /// <summary>
    /// Creates one uniquely named debugger session directory.
    /// </summary>
    /// <returns>The owned endpoint.</returns>
    internal static DebuggerTerminalEndpoint Create()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string directoryPath = Path.Join(
            temporaryRoot,
            $"{DirectoryPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        return new DebuggerTerminalEndpoint(temporaryRoot, directoryPath);
    }

    /// <summary>
    /// Removes only the exact uniquely named session directory owned by this instance.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        string? parent = Path.GetDirectoryName(DirectoryPath);
        string name = Path.GetFileName(DirectoryPath);
        if (!string.Equals(parent, _temporaryRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal) ||
            !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to remove an unrecognized debugger session directory: {DirectoryPath}");
        }

        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
