using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Dump;

/// <summary>
/// Supplies captured ClrMD memory and exact local native identities to the offline CoreCLR inspector.
/// </summary>
internal sealed class DumpCorDebugSource : ICorDebugDumpSource
{
    private readonly ClrInfo _runtime;
    private readonly string? _dacPath;
    private readonly IReadOnlyList<string> _binarySearchPaths;
    private readonly DumpMemoryFilter _memoryFilter;

    /// <summary>
    /// Binds one selected captured runtime and its optional explicit DAC selection.
    /// </summary>
    /// <param name="runtime">The caller-owned selected runtime description.</param>
    /// <param name="dacPath">An optional user-selected absolute DAC path.</param>
    /// <param name="binarySearchPaths">Optional user-selected local binary directories.</param>
    /// <param name="memoryFilter">The selected dump's captured storage-filter description.</param>
    internal DumpCorDebugSource(ClrInfo runtime, string? dacPath, IReadOnlyList<string>? binarySearchPaths = null,
        DumpMemoryFilter? memoryFilter = null)
    {
        _runtime = runtime;
        _dacPath = dacPath;
        _binarySearchPaths = DebuggerDumpBinarySearchPaths.Validate(binarySearchPaths);
        _memoryFilter = memoryFilter ?? DumpMemoryFilter.Read(runtime.DataTarget.DataReader.DisplayName, CancellationToken.None);
    }

    /// <inheritdoc />
    public OSPlatform Platform => _runtime.DataTarget.DataReader.TargetPlatform;

    /// <inheritdoc />
    public Architecture Architecture => _runtime.DataTarget.DataReader.Architecture;

    /// <inheritdoc />
    public int ReadMemory(ulong address, Span<byte> buffer) => _runtime.DataTarget.DataReader.Read(address, buffer);

    /// <inheritdoc />
    public bool IsMemoryFiltered(ulong address, ulong size) => _memoryFilter.Contains(address, size);

    /// <inheritdoc />
    public bool GetThreadContext(uint threadId, uint flags, Span<byte> context) =>
        _runtime.DataTarget.DataReader.GetThreadContext(threadId, flags, context);

    /// <inheritdoc />
    public IEnumerable<string> FindMetadataImages(string imagePath)
    {
        string name = Path.GetFileName(imagePath);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            yield break;
        }

        if (Path.IsPathFullyQualified(imagePath))
        {
            yield return imagePath;
        }

        foreach (string directory in _binarySearchPaths)
        {
            yield return Path.Join(directory, name);
        }

        int count = 0;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (ModuleInfo module in _runtime.DataTarget.EnumerateModules())
        {
            if (++count > 4096)
            {
                throw new InvalidDataException("Captured metadata discovery exceeds the 4096-module limit.");
            }
            if (string.Equals(name, Path.GetFileName(module.FileName), comparison))
            {
                yield return module.FileName;
            }
        }

        foreach (string path in CandidatePaths(name))
        {
            yield return path;
        }
    }

    /// <inheritdoc />
    public string ResolveWindowsLibrary(string name, bool runtimeIdentity, uint timestamp, uint imageSize)
    {
        if (!OperatingSystem.IsWindows() || Platform != OSPlatform.Windows || Architecture != RuntimeInformation.ProcessArchitecture)
        {
            throw new PlatformNotSupportedException("Offline CoreCLR inspection requires matching host and target architectures and operating systems.");
        }

        ValidateName(name);
        foreach (string path in CandidatePaths(name))
        {
            string indexedPath = runtimeIdentity ? Path.Join(Path.GetDirectoryName(path), "coreclr.dll") : path;
            if (File.Exists(path) && DumpWindowsImageLocator.MatchesImage(indexedPath, unchecked((int)timestamp), checked((int)imageSize)))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"The exact {name} required for offline frame inspection was not found.");
    }

    /// <inheritdoc />
    public string ResolveUnixLibrary(string name, bool runtimeIdentity, ReadOnlySpan<byte> buildId)
    {
        if (!RuntimeInformation.IsOSPlatform(Platform) || Architecture != RuntimeInformation.ProcessArchitecture)
        {
            throw new PlatformNotSupportedException("Offline CoreCLR inspection requires matching host and target architectures and operating systems.");
        }

        ValidateName(name);
        foreach (string path in CandidatePaths(name))
        {
            string indexedPath = runtimeIdentity
                ? Path.Join(Path.GetDirectoryName(path), Platform == OSPlatform.OSX ? "libcoreclr.dylib" : "libcoreclr.so")
                : path;
            if (File.Exists(path) && DumpNativeImageIdentity.Matches(indexedPath, Platform, Architecture, buildId))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"The exact {name} required for offline frame inspection was not found.");
    }

    private IEnumerable<string> CandidatePaths(string name)
    {
        bool dac = name is "mscordaccore.dll" or "libmscordaccore.so" or "libmscordaccore.dylib";
        if (dac && _dacPath is not null)
        {
            yield return _dacPath;
            yield break;
        }

        if (_dacPath is not null)
        {
            yield return Path.Join(Path.GetDirectoryName(_dacPath), name);
        }

        yield return Path.Join(RuntimeEnvironment.GetRuntimeDirectory(), name);
        string runtimeDirectory = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory());
        string? frameworkDirectory = Path.GetDirectoryName(runtimeDirectory);
        if (string.Equals(Path.GetFileName(frameworkDirectory), "Microsoft.NETCore.App", StringComparison.Ordinal) &&
            frameworkDirectory is not null)
        {
            int count = 0;
            foreach (string directory in Directory.EnumerateDirectories(frameworkDirectory))
            {
                if (++count > 1024)
                {
                    throw new InvalidDataException("Local runtime discovery exceeds the 1024-directory limit.");
                }

                yield return Path.Join(directory, name);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (string root in DumpWindowsRuntimeDirectories.GetDirectories())
            {
                yield return Path.Join(root, name);
            }
        }
    }

    private static void ValidateName(string name)
    {
        if (name is not ("mscordaccore.dll" or "mscordbi.dll" or "libmscordaccore.so" or "libmscordbi.so" or
            "libmscordaccore.dylib" or "libmscordbi.dylib"))
        {
            throw new InvalidDataException("The dump requested an unexpected executable debugger library.");
        }
    }
}
