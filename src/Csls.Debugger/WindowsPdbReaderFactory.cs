using Microsoft.DiaSymReader;
using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Creates native Windows PDB readers through the architecture-safe activation path.
/// </summary>
internal static class WindowsPdbReaderFactory
{
    private const string AlternativeLoadPathEnvironmentVariable =
        "MICROSOFT_DIASYMREADER_NATIVE_ALT_LOAD_PATH";
    private const string AlternativeLoadPathOnlyEnvironmentVariable =
        "MICROSOFT_DIASYMREADER_NATIVE_USE_ALT_LOAD_PATH_ONLY";
    private const string Arm64LibraryName = "Microsoft.DiaSymReader.Native.arm64.dll";
    private static readonly Lock s_environmentLock = new();

    /// <summary>
    /// Creates and initializes a Windows PDB reader for the current process architecture.
    /// </summary>
    /// <param name="pdbStream">The readable Windows PDB stream.</param>
    /// <param name="metadataProvider">The managed-module metadata provider.</param>
    /// <returns>The initialized unmanaged symbol reader.</returns>
    internal static ISymUnmanagedReader5 Create(
        Stream pdbStream,
        ISymReaderMetadataProvider metadataProvider)
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(
                pdbStream,
                metadataProvider);
        }

        string nativeDirectory = FindArm64NativeDirectory();
        lock (s_environmentLock)
        {
            string? previousPath = Environment.GetEnvironmentVariable(
                AlternativeLoadPathEnvironmentVariable);
            string? previousPathOnly = Environment.GetEnvironmentVariable(
                AlternativeLoadPathOnlyEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(
                    AlternativeLoadPathEnvironmentVariable,
                    nativeDirectory);
                Environment.SetEnvironmentVariable(
                    AlternativeLoadPathOnlyEnvironmentVariable,
                    "1");
                return SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(
                    pdbStream,
                    metadataProvider,
                    SymUnmanagedReaderCreationOptions.UseAlternativeLoadPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    AlternativeLoadPathEnvironmentVariable,
                    previousPath);
                Environment.SetEnvironmentVariable(
                    AlternativeLoadPathOnlyEnvironmentVariable,
                    previousPathOnly);
            }
        }
    }

    private static string FindArm64NativeDirectory()
    {
        var directories = new List<string>
        {
            AppContext.BaseDirectory,
            Path.Join(AppContext.BaseDirectory, "runtimes", "win-arm64", "native"),
            Path.Join(AppContext.BaseDirectory, "runtimes", "win", "native")
        };
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirectories)
        {
            directories.AddRange(searchDirectories.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(directory => File.Exists(Path.Join(directory, Arm64LibraryName)))
            .FirstOrDefault() ?? throw new DllNotFoundException(
                $"The ARM64 Windows PDB reader '{Arm64LibraryName}' was not found in the native dependency search path.");
    }
}
