namespace Csls.Debugger;

/// <summary>
/// Identifies one captured managed module's layout and exact PE build identity.
/// </summary>
/// <param name="Address">The captured module's base address.</param>
/// <param name="Size">The captured image range in bytes.</param>
/// <param name="IsLoadedImage">Whether the module uses virtual rather than file layout.</param>
/// <param name="Path">The recorded module path used for local image discovery.</param>
/// <param name="Timestamp">The recorded PE build timestamp.</param>
/// <param name="ImageSize">The recorded PE mapped image size.</param>
public sealed record CorDebugDumpModuleInfo(ulong Address, ulong Size, bool IsLoadedImage,
    string Path, uint Timestamp, uint ImageSize);
