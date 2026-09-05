using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Validates and configures the packaged .NET debugger-shim native library.
/// </summary>
internal static class DbgShimLibrary
{
    static DbgShimLibrary()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(DbgShimLibrary).Assembly,
            DbgShimNativeLibraryResolver.Resolve);
    }

    /// <summary>
    /// Loads the packaged shim and verifies its required current-runtime export.
    /// </summary>
    internal static void VerifyPlatformSupport()
    {
        nint library = DbgShimNativeLibraryResolver.Resolve(
            "dbgshim",
            typeof(DbgShimLibrary).Assembly,
            searchPath: null);
        try
        {
            string[] requiredExports =
            [
                "CreateProcessForLaunch",
                "ResumeProcess",
                "CloseResumeHandle",
                "RegisterForRuntimeStartup",
                "UnregisterForRuntimeStartup"
            ];
            foreach (string requiredExport in requiredExports)
            {
                _ = NativeLibrary.GetExport(library, requiredExport);
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }
}
