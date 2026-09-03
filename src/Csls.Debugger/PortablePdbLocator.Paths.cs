namespace Csls.Debugger;

/// <summary>
/// Generates canonical Microsoft symbol-store paths for Portable PDB lookup.
/// </summary>
internal sealed partial class PortablePdbLocator
{
    private static IEnumerable<string> GetIdentities(PortablePdbReference reference)
    {
        yield return reference.PortableIdentity;
        if (!string.Equals(
            reference.PortableIdentity,
            reference.WindowsIdentity,
            StringComparison.OrdinalIgnoreCase))
        {
            yield return reference.WindowsIdentity;
        }
    }

    private static string GetStorePath(string root, string fileName, string identity) =>
        Path.Join(root, NormalizeStoreFileName(fileName), identity, NormalizeStoreFileName(fileName));

    private static string GetStoreIndex(string fileName, string identity) =>
        $"{NormalizeStoreFileName(fileName)}/{identity}/{NormalizeStoreFileName(fileName)}";

    private static string NormalizeStoreFileName(string fileName) => string.Create(
        fileName.Length,
        fileName,
        static (destination, source) =>
        {
            for (int index = 0; index < source.Length; index++)
            {
                char character = source[index];
                destination[index] = character is >= 'A' and <= 'Z'
                    ? (char)(character + ('a' - 'A'))
                    : character;
            }
        });

    private static string NormalizeAbsolutePath(string path, string option)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{option} must be an absolute directory path.");
        }

        return Path.GetFullPath(path);
    }

    private static string GetDefaultCachePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Join(Path.GetTempPath(), "SymbolCache");
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? Path.Join(Path.GetTempPath(), "csls", "symbolcache")
            : Path.Join(profile, ".dotnet", "symbolcache");
    }
}
