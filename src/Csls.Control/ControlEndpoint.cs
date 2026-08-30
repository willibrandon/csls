using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Csls.Control;

/// <summary>
/// Resolves and secures the per-user Unix-domain-socket namespace used by csls sessions.
/// </summary>
public static class ControlEndpoint
{
    /// <summary>
    /// Names the optional absolute control-socket directory environment variable.
    /// </summary>
    public const string SocketDirectoryEnvironmentVariable =
        "CSLS_CONTROL_SOCKET_DIRECTORY";

    /// <summary>
    /// Gets the absolute directory containing live csls control sockets.
    /// </summary>
    /// <returns>The per-user socket directory.</returns>
    public static string GetSocketDirectory()
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable(
            SocketDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            if (!Path.IsPathFullyQualified(configuredDirectory))
            {
                throw new InvalidDataException(
                    $"{SocketDirectoryEnvironmentVariable} must be an absolute path: " +
                    configuredDirectory);
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredDirectory));
        }

        string userProfile = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify)
            : UnixUserProfileResolver.GetCurrentUserHomeDirectory();
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException(
                "The current user profile directory is unavailable.");
        }

        return Path.Join(userProfile, ".csls", "sockets");
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
        EnsureSocketDirectory();
        string socketPath = GetSocketPath(processId);
        File.Delete(socketPath);
        return socketPath;
    }

    /// <summary>
    /// Creates and secures the private socket directory for discovery and listening.
    /// </summary>
    /// <returns>The absolute private socket directory.</returns>
    internal static string EnsureSocketDirectory()
    {
        string socketDirectory = GetSocketDirectory();
        Directory.CreateDirectory(socketDirectory);
        var directory = new DirectoryInfo(socketDirectory);
        if (directory.LinkTarget is not null)
        {
            throw new IOException(
                $"The csls socket directory must not be a symbolic link: {socketDirectory}");
        }

        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsDirectory(directory);
        }
        else
        {
            File.SetUnixFileMode(
                socketDirectory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        return socketDirectory;
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

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsDirectory(DirectoryInfo directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User
            ?? throw new InvalidOperationException(
                "The current Windows user has no security identifier.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(
            new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        directory.SetAccessControl(security);
    }
}
