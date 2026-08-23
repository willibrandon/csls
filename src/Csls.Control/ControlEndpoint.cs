using System.Security.Cryptography;
using System.Text;

namespace Csls.Control;

/// <summary>
/// Resolves and secures the per-user Unix-domain-socket namespace used by csls sessions.
/// </summary>
public static class ControlEndpoint
{
    /// <summary>
    /// Gets the absolute directory containing live csls control sockets.
    /// </summary>
    /// <returns>The per-user socket directory.</returns>
    public static string GetSocketDirectory()
    {
        string temporaryDirectory = OperatingSystem.IsWindows()
            ? Path.GetTempPath()
            : "/tmp";
        return Path.Join(temporaryDirectory, $"csls-{GetUserKey()}");
    }

    /// <summary>
    /// Gets the absolute control-socket path for a language-server process.
    /// </summary>
    /// <param name="processId">The positive language-server process identifier.</param>
    /// <returns>The absolute Unix-domain-socket path.</returns>
    public static string GetSocketPath(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return Path.Join(GetSocketDirectory(), $"{processId}.csls.socket");
    }

    /// <summary>
    /// Creates the private socket directory and returns a clean path for the current process.
    /// </summary>
    /// <param name="processId">The current language-server process identifier.</param>
    /// <returns>The prepared absolute socket path.</returns>
    internal static string PrepareSocketPath(int processId)
    {
        string socketDirectory = GetSocketDirectory();
        Directory.CreateDirectory(socketDirectory);
        if (!OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(socketDirectory);
            if (directory.LinkTarget is not null)
            {
                throw new IOException(
                    $"The csls socket directory must not be a symbolic link: {socketDirectory}");
            }

            File.SetUnixFileMode(
                socketDirectory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        string socketPath = GetSocketPath(processId);
        File.Delete(socketPath);
        return socketPath;
    }

    /// <summary>
    /// Restricts a bound Unix-domain socket to the current user on Unix hosts.
    /// </summary>
    /// <param name="socketPath">The absolute path of the bound socket.</param>
    internal static void RestrictSocket(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string GetUserKey()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
