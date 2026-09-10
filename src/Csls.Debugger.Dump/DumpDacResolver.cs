using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Csls.Debugger.Dump;

/// <summary>
/// Resolves runtime DACs against explicit user selections or trusted installed runtime roots.
/// </summary>
internal static class DumpDacResolver
{
    /// <summary>
    /// Selects an exact DAC image without executing paths supplied by the captured process.
    /// </summary>
    /// <param name="runtime">The captured runtime identity.</param>
    /// <param name="explicitPath">The optional absolute DAC path selected by the user.</param>
    /// <returns>The identity-validated DAC path.</returns>
    internal static string Resolve(ClrInfo runtime, string? explicitPath)
    {
        DebugLibraryInfo[] libraries = [.. runtime.DebuggingLibraries.Where(library =>
            library.Kind == DebugLibraryKind.Dac && RuntimeInformation.IsOSPlatform(library.Platform) &&
            library.TargetArchitecture == RuntimeInformation.ProcessArchitecture)];
        if (explicitPath is not null)
        {
            bool hasLibraryIdentity = libraries.Any(library => library.ArchivedUnder == SymbolProperties.Self);
            bool matches = hasLibraryIdentity
                ? libraries.Any(library => MatchesExplicit(library, explicitPath))
                : MatchesInstalledDac(runtime, explicitPath);
            if (!matches)
            {
                throw new InvalidDataException("The selected DAC does not match the dump's debugging-library identity.");
            }

            return explicitPath;
        }

        var source = new DumpCorDebugSource(runtime, dacPath: null);
        string name = OperatingSystem.IsWindows() ? "mscordaccore.dll" :
            OperatingSystem.IsMacOS() ? "libmscordaccore.dylib" : "libmscordaccore.so";
        FileNotFoundException? lastFailure = null;
        foreach (DebugLibraryInfo library in libraries.OrderBy(library => library.ArchivedUnder == SymbolProperties.Self ? 0 : 1))
        {
            if (library.ArchivedUnder is not (SymbolProperties.Self or SymbolProperties.Coreclr))
            {
                continue;
            }

            bool runtimeIdentity = library.ArchivedUnder == SymbolProperties.Coreclr;
            try
            {
                return OperatingSystem.IsWindows()
                    ? source.ResolveWindowsLibrary(name, runtimeIdentity, unchecked((uint)library.IndexTimeStamp),
                        checked((uint)library.IndexFileSize))
                    : source.ResolveUnixLibrary(name, runtimeIdentity, library.IndexBuildId.AsSpan());
            }
            catch (FileNotFoundException exception)
            {
                // A runtime may provide multiple symbol keys; each candidate must match its own key.
                lastFailure = exception;
            }
        }

        throw new FileNotFoundException($"The exact {name} required for dump inspection was not found in the local runtime installations.",
            name, lastFailure);
    }

    private static bool MatchesExplicit(DebugLibraryInfo library, string path)
    {
        if (library.ArchivedUnder != SymbolProperties.Self)
        {
            return false;
        }

        return OperatingSystem.IsWindows()
            ? DumpWindowsImageLocator.MatchesImage(path, library.IndexTimeStamp, library.IndexFileSize)
            : DumpNativeImageIdentity.Matches(path, library.Platform, library.TargetArchitecture, library.IndexBuildId.AsSpan());
    }

    private static bool MatchesInstalledDac(ClrInfo runtime, string path)
    {
        // Runtime-indexed dumps identify the matching installed CoreCLR, not the DAC itself.
        // An explicit DAC must then equal that installation's DAC before native code can load it.
        string installed = Resolve(runtime, explicitPath: null);
        using FileStream selected = File.OpenRead(path);
        using FileStream trusted = File.OpenRead(installed);
        return selected.Length == trusted.Length &&
            SHA256.HashData(selected).AsSpan().SequenceEqual(SHA256.HashData(trusted));
    }
}
