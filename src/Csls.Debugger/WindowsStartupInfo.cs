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
    internal uint _size;

    /// <summary>
    /// Gets or sets the reserved desktop-station pointer.
    /// </summary>
    internal nint _reserved;

    /// <summary>
    /// Gets or sets the target desktop name pointer.
    /// </summary>
    internal nint _desktop;

    /// <summary>
    /// Gets or sets the target window title pointer.
    /// </summary>
    internal nint _title;

    /// <summary>
    /// Gets or sets the requested window horizontal position.
    /// </summary>
    internal uint _x;

    /// <summary>
    /// Gets or sets the requested window vertical position.
    /// </summary>
    internal uint _y;

    /// <summary>
    /// Gets or sets the requested window width.
    /// </summary>
    internal uint _xSize;

    /// <summary>
    /// Gets or sets the requested window height.
    /// </summary>
    internal uint _ySize;

    /// <summary>
    /// Gets or sets the requested console buffer width.
    /// </summary>
    internal uint _xCountChars;

    /// <summary>
    /// Gets or sets the requested console buffer height.
    /// </summary>
    internal uint _yCountChars;

    /// <summary>
    /// Gets or sets the requested console fill attributes.
    /// </summary>
    internal uint _fillAttribute;

    /// <summary>
    /// Gets or sets the startup option flags.
    /// </summary>
    internal uint _flags;

    /// <summary>
    /// Gets or sets the requested window display state.
    /// </summary>
    internal ushort _showWindow;

    /// <summary>
    /// Gets or sets the reserved byte count.
    /// </summary>
    internal ushort _reservedByteCount;

    /// <summary>
    /// Gets or sets the reserved byte-buffer pointer.
    /// </summary>
    internal nint _reservedBytes;

    /// <summary>
    /// Gets or sets the inherited standard-input handle.
    /// </summary>
    internal nint _standardInput;

    /// <summary>
    /// Gets or sets the inherited standard-output handle.
    /// </summary>
    internal nint _standardOutput;

    /// <summary>
    /// Gets or sets the inherited standard-error handle.
    /// </summary>
    internal nint _standardError;
}
