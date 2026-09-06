using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Supplies immutable captured memory and identity-validated debugger libraries for offline inspection.
/// </summary>
public interface ICorDebugDumpSource
{
    /// <summary>
    /// Gets the operating system of the captured target.
    /// </summary>
    OSPlatform Platform { get; }

    /// <summary>
    /// Gets the processor architecture of the captured target.
    /// </summary>
    Architecture Architecture { get; }

    /// <summary>
    /// Reads available captured bytes starting at a target virtual address.
    /// </summary>
    /// <param name="address">The target virtual address.</param>
    /// <param name="buffer">The caller-owned destination.</param>
    /// <returns>The number of consecutive bytes read, or zero when memory is absent.</returns>
    int ReadMemory(ulong address, Span<byte> buffer);

    /// <summary>
    /// Copies a captured operating-system thread context into the caller's buffer.
    /// </summary>
    /// <param name="threadId">The captured operating-system thread identifier.</param>
    /// <param name="flags">The requested platform context flags.</param>
    /// <param name="context">The caller-owned native context buffer.</param>
    /// <returns>Whether the requested context was available.</returns>
    bool GetThreadContext(uint threadId, uint flags, Span<byte> context);

    /// <summary>
    /// Resolves an exact Windows debugger-library request to a trusted absolute path.
    /// </summary>
    /// <param name="name">The requested library basename.</param>
    /// <param name="runtimeIdentity">Whether the supplied identity describes CoreCLR rather than the library.</param>
    /// <param name="timestamp">The requested PE timestamp.</param>
    /// <param name="imageSize">The requested PE mapped image size.</param>
    /// <returns>The verified library path.</returns>
    string ResolveWindowsLibrary(string name, bool runtimeIdentity, uint timestamp, uint imageSize);

    /// <summary>
    /// Resolves an exact Unix debugger-library request to a trusted absolute path.
    /// </summary>
    /// <param name="name">The requested library basename.</param>
    /// <param name="runtimeIdentity">Whether the supplied identity describes CoreCLR rather than the library.</param>
    /// <param name="buildId">The requested ELF build identifier or Mach-O UUID.</param>
    /// <returns>The verified library path.</returns>
    string ResolveUnixLibrary(string name, bool runtimeIdentity, ReadOnlySpan<byte> buildId);
}
