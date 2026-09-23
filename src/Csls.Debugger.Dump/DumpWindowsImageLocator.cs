using Microsoft.Diagnostics.Runtime;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger.Dump;

/// <summary>
/// Resolves dump image identities against local Windows runtime installations.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DumpWindowsImageLocator : IFileLocator
{
    private readonly IReadOnlyList<string> _directories = DumpWindowsRuntimeDirectories.GetDirectories();
    private readonly IReadOnlyList<string> _binarySearchPaths;

    /// <summary>
    /// Binds explicit application binary roots while retaining trusted native runtime discovery.
    /// </summary>
    /// <param name="binarySearchPaths">Optional absolute local application binary directories.</param>
    internal DumpWindowsImageLocator(IReadOnlyList<string>? binarySearchPaths = null)
    {
        _binarySearchPaths = DebuggerDumpBinarySearchPaths.Validate(binarySearchPaths);
    }

    /// <inheritdoc />
    public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties)
    {
        string name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        // ClrMD also asks for DACs under CoreCLR's symbol key. A local image is
        // accepted only under its own PE identity, including when checkProperties is false.
        string? binary = _binarySearchPaths.Select(directory => Path.Join(directory, name))
            .FirstOrDefault(candidate => MatchesImage(candidate, buildTimeStamp, imageSize, allowManaged: true));
        return binary ?? _directories.Select(directory => Path.Join(directory, name))
            .FirstOrDefault(candidate => MatchesImage(candidate, buildTimeStamp, imageSize));
    }

    /// <inheritdoc />
    public string? FindPEImage(string fileName, SymbolProperties archivedUnder,
        ImmutableArray<byte> buildIdOrUUID, OSPlatform originalPlatform, bool checkProperties) => null;

    /// <summary>
    /// Verifies the image's build identity and compatibility with the current dump worker architecture.
    /// </summary>
    /// <param name="path">The local image to inspect without loading code.</param>
    /// <param name="timeStamp">The expected PE build timestamp.</param>
    /// <param name="imageSize">The expected mapped image size.</param>
    /// <param name="allowManaged">Whether an IL-only AnyCPU application image is also eligible.</param>
    /// <returns>Whether the image has the exact required identity and architecture.</returns>
    internal static bool MatchesImage(string path, int timeStamp, int imageSize, bool allowManaged = false)
    {
        if (imageSize <= 0)
        {
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (allowManaged && !reader.HasMetadata)
            {
                return false;
            }
            PEHeaders headers = reader.PEHeaders;
            Machine expectedMachine = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => Machine.Amd64,
                Architecture.Arm64 => Machine.Arm64,
                Architecture.X86 => Machine.I386,
                _ => Machine.Unknown
            };
            bool anyCpu = allowManaged && headers.CoffHeader.Machine == Machine.I386 &&
                headers.CorHeader is { } cor && (cor.Flags & (CorFlags.ILOnly | CorFlags.Requires32Bit)) == CorFlags.ILOnly;
            return expectedMachine != Machine.Unknown && (headers.CoffHeader.Machine == expectedMachine || anyCpu) &&
                headers.CoffHeader.TimeDateStamp == timeStamp && headers.PEHeader?.SizeOfImage == imageSize;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }
}
