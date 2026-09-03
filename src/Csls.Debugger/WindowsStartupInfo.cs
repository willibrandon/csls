using System.Runtime.InteropServices;

namespace Csls.Debugger;

/// <summary>
/// Describes Windows process startup state passed to CreateProcessW.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowsStartupInfo
{
    /// <summary>
    /// Gets or sets the complete extended startup structure size.
    /// </summary>
    internal uint Size;

    /// <summary>
    /// Gets or sets the reserved desktop-station pointer.
    /// </summary>
    internal nint Reserved;

    /// <summary>
    /// Gets or sets the target desktop name pointer.
    /// </summary>
    internal nint Desktop;

    /// <summary>
    /// Gets or sets the target window title pointer.
    /// </summary>
    internal nint Title;

    /// <summary>
    /// Gets or sets the requested window horizontal position.
    /// </summary>
    internal uint X;

    /// <summary>
    /// Gets or sets the requested window vertical position.
    /// </summary>
    internal uint Y;

    /// <summary>
    /// Gets or sets the requested window width.
    /// </summary>
    internal uint XSize;

    /// <summary>
    /// Gets or sets the requested window height.
    /// </summary>
    internal uint YSize;

    /// <summary>
    /// Gets or sets the requested console buffer width.
    /// </summary>
    internal uint XCountChars;

    /// <summary>
    /// Gets or sets the requested console buffer height.
    /// </summary>
    internal uint YCountChars;

    /// <summary>
    /// Gets or sets the requested console fill attributes.
    /// </summary>
    internal uint FillAttribute;

    /// <summary>
    /// Gets or sets the startup option flags.
    /// </summary>
    internal uint Flags;

    /// <summary>
    /// Gets or sets the requested window display state.
    /// </summary>
    internal ushort ShowWindow;

    /// <summary>
    /// Gets or sets the reserved byte count.
    /// </summary>
    internal ushort ReservedByteCount;

    /// <summary>
    /// Gets or sets the reserved byte-buffer pointer.
    /// </summary>
    internal nint ReservedBytes;

    /// <summary>
    /// Gets or sets the inherited standard-input handle.
    /// </summary>
    internal nint StandardInput;

    /// <summary>
    /// Gets or sets the inherited standard-output handle.
    /// </summary>
    internal nint StandardOutput;

    /// <summary>
    /// Gets or sets the inherited standard-error handle.
    /// </summary>
    internal nint StandardError;
}
