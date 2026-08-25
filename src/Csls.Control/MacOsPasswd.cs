using System.Runtime.InteropServices;

namespace Csls.Control;

/// <summary>
/// Matches the macOS passwd record populated by getpwuid_r.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MacOsPasswd
{
    /// <summary>
    /// Stores the native account-name pointer.
    /// </summary>
    internal nint _name;

    /// <summary>
    /// Stores the native password-field pointer.
    /// </summary>
    internal nint _password;

    /// <summary>
    /// Stores the native account user identifier.
    /// </summary>
    internal uint _userId;

    /// <summary>
    /// Stores the native account group identifier.
    /// </summary>
    internal uint _groupId;

    /// <summary>
    /// Stores the native password-change timestamp.
    /// </summary>
    internal long _change;

    /// <summary>
    /// Stores the native account-class pointer.
    /// </summary>
    internal nint _class;

    /// <summary>
    /// Stores the native account information pointer.
    /// </summary>
    internal nint _gecos;

    /// <summary>
    /// Stores the native home-directory pointer.
    /// </summary>
    internal nint _directory;

    /// <summary>
    /// Stores the native login-shell pointer.
    /// </summary>
    internal nint _shell;

    /// <summary>
    /// Stores the native account-expiration timestamp.
    /// </summary>
    internal long _expire;
}
