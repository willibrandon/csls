using System.Runtime.InteropServices;

namespace Csls.Tests;

/// <summary>
/// Sends deterministic keyboard chords to the focused window on an isolated X display.
/// </summary>
internal static partial class X11Input
{
    private const ulong ControlLeftKeySym = 0xffe3;
    private const ulong AltLeftKeySym = 0xffe9;
    private const ulong ShiftLeftKeySym = 0xffe1;
    private const ulong F12KeySym = 0xffc9;
    private const int RevertToParent = 2;

    /// <summary>
    /// Raises and focuses the first X window whose title contains the requested text.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    /// <param name="titleText">Text required in the target window title.</param>
    internal static void FocusWindow(string displayName, string titleText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleText);
        nint display = OpenDisplay(displayName);
        if (display == 0)
        {
            throw new InvalidOperationException($"The X display is unavailable: {displayName}");
        }

        try
        {
            nuint rootWindow = DefaultRootWindow(display);
            var observedTitles = new List<string>();
            nuint targetWindow = FindWindow(
                display,
                rootWindow,
                titleText,
                observedTitles);
            if (targetWindow == 0)
            {
                throw new InvalidOperationException(
                    $"No X window title contains '{titleText}'. Observed: " +
                    string.Join(", ", observedTitles));
            }

            _ = RaiseWindow(display, targetWindow);
            _ = SetInputFocus(display, targetWindow, RevertToParent, time: 0);
            _ = Flush(display);
        }
        finally
        {
            _ = CloseDisplay(display);
        }
    }

    /// <summary>
    /// Sends one Control-modified character to the focused window and flushes the X event queue.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    /// <param name="character">The lowercase ASCII key to press.</param>
    internal static void SendControlCharacter(string displayName, char character)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        nint display = OpenDisplay(displayName);
        if (display == 0)
        {
            throw new InvalidOperationException($"The X display is unavailable: {displayName}");
        }

        try
        {
            SendControlCharacter(display, character);
            _ = Flush(display);
        }
        finally
        {
            _ = CloseDisplay(display);
        }
    }

    /// <summary>
    /// Sends a two-part Control chord through one ordered X connection.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    /// <param name="firstCharacter">The first lowercase ASCII key to press.</param>
    /// <param name="secondCharacter">The second lowercase ASCII key to press.</param>
    internal static void SendControlSequence(
        string displayName,
        char firstCharacter,
        char secondCharacter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        nint display = OpenDisplay(displayName);
        if (display == 0)
        {
            throw new InvalidOperationException($"The X display is unavailable: {displayName}");
        }

        try
        {
            SendControlCharacter(display, firstCharacter);
            SendControlCharacter(display, secondCharacter);
            _ = Flush(display);
        }
        finally
        {
            _ = CloseDisplay(display);
        }
    }

    /// <summary>
    /// Sends F12 to the focused window and flushes the X event queue.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    internal static void SendF12(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        nint display = OpenDisplay(displayName);
        if (display == 0)
        {
            throw new InvalidOperationException($"The X display is unavailable: {displayName}");
        }

        try
        {
            byte functionKey = KeySymToKeycode(display, F12KeySym);
            if (functionKey == 0)
            {
                throw new InvalidOperationException("The X display cannot map F12.");
            }

            SendKey(display, functionKey, isPressed: true);
            SendKey(display, functionKey, isPressed: false);
            _ = Flush(display);
        }
        finally
        {
            _ = CloseDisplay(display);
        }
    }

    /// <summary>
    /// Sends Alt+Shift+F12 to the focused window and flushes the X event queue.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    internal static void SendFindAllReferences(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        nint display = OpenDisplay(displayName);
        if (display == 0)
        {
            throw new InvalidOperationException($"The X display is unavailable: {displayName}");
        }

        try
        {
            byte altKey = KeySymToKeycode(display, AltLeftKeySym);
            byte shiftKey = KeySymToKeycode(display, ShiftLeftKeySym);
            byte functionKey = KeySymToKeycode(display, F12KeySym);
            if (altKey == 0 || shiftKey == 0 || functionKey == 0)
            {
                throw new InvalidOperationException("The X display cannot map Alt+Shift+F12.");
            }

            SendKey(display, altKey, isPressed: true);
            SendKey(display, shiftKey, isPressed: true);
            SendKey(display, functionKey, isPressed: true);
            SendKey(display, functionKey, isPressed: false);
            SendKey(display, shiftKey, isPressed: false);
            SendKey(display, altKey, isPressed: false);
            _ = Flush(display);
        }
        finally
        {
            _ = CloseDisplay(display);
        }
    }

    private static void SendKey(nint display, byte keyCode, bool isPressed)
    {
        if (FakeKeyEvent(display, keyCode, isPressed ? 1 : 0, delay: 0) == 0)
        {
            throw new InvalidOperationException("The XTEST key event was rejected.");
        }
    }

    private static void SendControlCharacter(nint display, char character)
    {
        if (!char.IsAsciiLetterLower(character))
        {
            throw new ArgumentOutOfRangeException(
                nameof(character),
                character,
                "The X11 test input must be a lowercase ASCII letter.");
        }

        byte controlKey = KeySymToKeycode(display, ControlLeftKeySym);
        byte characterKey = KeySymToKeycode(display, character);
        if (controlKey == 0 || characterKey == 0)
        {
            throw new InvalidOperationException(
                $"The X display cannot map Control+{character}.");
        }

        SendKey(display, controlKey, isPressed: true);
        SendKey(display, characterKey, isPressed: true);
        SendKey(display, characterKey, isPressed: false);
        SendKey(display, controlKey, isPressed: false);
    }

    private static nuint FindWindow(
        nint display,
        nuint window,
        string titleText,
        List<string> observedTitles)
    {
        string? title = GetWindowTitle(display, window);
        if (!string.IsNullOrWhiteSpace(title))
        {
            observedTitles.Add(title);
        }

        if (title is not null && title.Contains(titleText, StringComparison.Ordinal))
        {
            return window;
        }

        if (QueryTree(
                display,
                window,
                out _,
                out _,
                out nint childrenPointer,
                out uint childCount) == 0)
        {
            return 0;
        }

        try
        {
            for (uint index = 0; index < childCount; index++)
            {
                nint childPointer = Marshal.ReadIntPtr(
                    childrenPointer,
                    checked((int)index * IntPtr.Size));
                nuint childWindow = unchecked((nuint)childPointer);
                nuint match = FindWindow(
                    display,
                    childWindow,
                    titleText,
                    observedTitles);
                if (match != 0)
                {
                    return match;
                }
            }
        }
        finally
        {
            if (childrenPointer != 0)
            {
                _ = Free(childrenPointer);
            }
        }

        return 0;
    }

    private static string? GetWindowTitle(nint display, nuint window)
    {
        nuint nameAtom = InternAtom(display, "_NET_WM_NAME", onlyIfExists: 1);
        if (nameAtom != 0 &&
            GetWindowProperty(
                display,
                window,
                nameAtom,
                offset: 0,
                length: 1024,
                delete: 0,
                requestedType: 0,
                out _,
                out int format,
                out nuint itemCount,
                out _,
                out nint propertyData) == 0 &&
            propertyData != 0)
        {
            try
            {
                if (format == 8 && itemCount > 0)
                {
                    return Marshal.PtrToStringUTF8(propertyData, checked((int)itemCount));
                }
            }
            finally
            {
                _ = Free(propertyData);
            }
        }

        if (FetchName(display, window, out nint namePointer) == 0 || namePointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringAnsi(namePointer);
        }
        finally
        {
            _ = Free(namePointer);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XOpenDisplay", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint OpenDisplay(string displayName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static partial int CloseDisplay(nint display);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XDefaultRootWindow")]
    private static partial nuint DefaultRootWindow(nint display);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XFetchName")]
    private static partial int FetchName(nint display, nuint window, out nint windowName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint InternAtom(
        nint display,
        string atomName,
        int onlyIfExists);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XGetWindowProperty")]
    private static partial int GetWindowProperty(
        nint display,
        nuint window,
        nuint property,
        nint offset,
        nint length,
        int delete,
        nuint requestedType,
        out nuint actualType,
        out int actualFormat,
        out nuint itemCount,
        out nuint bytesAfter,
        out nint propertyData);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XQueryTree")]
    private static partial int QueryTree(
        nint display,
        nuint window,
        out nuint root,
        out nuint parent,
        out nint children,
        out uint childCount);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XRaiseWindow")]
    private static partial int RaiseWindow(nint display, nuint window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XSetInputFocus")]
    private static partial int SetInputFocus(
        nint display,
        nuint focus,
        int revertTo,
        ulong time);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XFree")]
    private static partial int Free(nint data);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XKeysymToKeycode")]
    private static partial byte KeySymToKeycode(nint display, ulong keySym);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libX11.so.6", EntryPoint = "XFlush")]
    private static partial int Flush(nint display);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libXtst.so.6", EntryPoint = "XTestFakeKeyEvent")]
    private static partial int FakeKeyEvent(
        nint display,
        uint keyCode,
        int isPressed,
        ulong delay);
}
