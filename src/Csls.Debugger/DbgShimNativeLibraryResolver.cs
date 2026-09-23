using System.Reflection;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Resolves only the packaged debugger shim from the application directory.
/// </summary>
internal static class DbgShimNativeLibraryResolver
{
    /// <summary>
    /// Resolves the debugger shim to an exact application-relative native asset.
    /// </summary>
    /// <param name="libraryName">The logical native library name.</param>
    /// <param name="assembly">The assembly requesting the native import.</param>
    /// <param name="searchPath">The runtime-provided fallback search policy.</param>
    /// <returns>The loaded native module, or zero for unrelated libraries.</returns>
    internal static nint Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        _ = assembly;
        _ = searchPath;
        if (!string.Equals(libraryName, "dbgshim", StringComparison.Ordinal))
        {
            return 0;
        }

        string fileName = OperatingSystem.IsWindows()
            ? "dbgshim.dll"
            : OperatingSystem.IsLinux()
                ? "libdbgshim.so"
                : OperatingSystem.IsMacOS()
                    ? "libdbgshim.dylib"
                    : throw new PlatformNotSupportedException(
                        "The .NET debugger shim supports Windows, Linux, and macOS.");
        string publishedPath = Path.GetFullPath(fileName, AppContext.BaseDirectory);
        string portableBuildPath = Path.GetFullPath(
            Path.Join(
                "runtimes",
                RuntimeInformation.RuntimeIdentifier,
                "native",
                fileName),
            AppContext.BaseDirectory);
        string? path = File.Exists(publishedPath)
            ? publishedPath
            : File.Exists(portableBuildPath)
                ? portableBuildPath
                : null;
        if (path is null)
        {
            throw new DllNotFoundException(
                $"The packaged .NET debugger shim was not found at '{publishedPath}' " +
                $"or '{portableBuildPath}'.");
        }

        return NativeLibrary.Load(path);
    }
}
