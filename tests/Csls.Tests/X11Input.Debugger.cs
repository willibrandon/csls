namespace Csls.Tests;

/// <summary>
/// Sends debugger-specific keys to a focused window on an isolated X display.
/// </summary>
internal static partial class X11Input
{
    private const ulong EnterKeySym = 0xff0d;
    private const ulong F4KeySym = 0xffc1;
    private const ulong F9KeySym = 0xffc6;

    /// <summary>
    /// Sends F4 to open Zed's debugger task picker.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    internal static void SendF4(string displayName) => SendKeySym(displayName, F4KeySym, "F4");

    /// <summary>
    /// Sends F9 to toggle a source breakpoint in Zed.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    internal static void SendF9(string displayName) => SendKeySym(displayName, F9KeySym, "F9");

    /// <summary>
    /// Sends Enter to confirm the selected Zed debugger task.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    internal static void SendEnter(string displayName) =>
        SendKeySym(displayName, EnterKeySym, "Enter");

    /// <summary>
    /// Sends Shift+F5 to stop the active Zed debugger session.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    internal static void SendShiftF5(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        lock (s_interopLock)
        {
            nint display = OpenDisplay(displayName);
            if (display == 0)
            {
                throw new InvalidOperationException(
                    $"The X display is unavailable: {displayName}");
            }

            try
            {
                byte shift = KeySymToKeycode(display, ShiftLeftKeySym);
                byte function = KeySymToKeycode(display, 0xffc2);
                if (shift == 0 || function == 0)
                {
                    throw new InvalidOperationException("The X display cannot map Shift+F5.");
                }

                SendKey(display, shift, isPressed: true);
                SendKey(display, function, isPressed: true);
                SendKey(display, function, isPressed: false);
                SendKey(display, shift, isPressed: false);
                _ = Flush(display);
            }
            finally
            {
                _ = CloseDisplay(display);
            }
        }
    }

    private static void SendKeySym(string displayName, ulong keySym, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        lock (s_interopLock)
        {
            nint display = OpenDisplay(displayName);
            if (display == 0)
            {
                throw new InvalidOperationException(
                    $"The X display is unavailable: {displayName}");
            }

            try
            {
                byte key = KeySymToKeycode(display, keySym);
                if (key == 0)
                {
                    throw new InvalidOperationException($"The X display cannot map {name}.");
                }

                SendKey(display, key, isPressed: true);
                SendKey(display, key, isPressed: false);
                _ = Flush(display);
            }
            finally
            {
                _ = CloseDisplay(display);
            }
        }
    }
}
