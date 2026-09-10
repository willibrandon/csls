using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Owns a Windows Restart Manager session used only to inspect file owners.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class DebuggerRestartManagerSession : SafeHandle
{
    /// <summary>
    /// Creates an empty owner for a native inspection session.
    /// </summary>
    internal DebuggerRestartManagerSession() : base(-1, ownsHandle: true)
    {
    }

    /// <inheritdoc />
    public override bool IsInvalid => handle == -1;

    /// <summary>
    /// Acquires a native session with an owner allocated before the native call.
    /// </summary>
    /// <returns>The exclusively owned inspection session.</returns>
    internal static DebuggerRestartManagerSession Open()
    {
        var session = new DebuggerRestartManagerSession();
        try
        {
            char* key = stackalloc char[33];
            uint result = StartSession(out uint identifier, 0, key);
            ThrowIfFailed(result, "RmStartSession");
            session.SetHandle(unchecked((nint)identifier));
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Queries a bounded list of processes using one absolute file path.
    /// </summary>
    /// <param name="path">The validated absolute file path.</param>
    /// <returns>The native identifiers of the file's current owners.</returns>
    internal int[] FindOwners(string path)
    {
        bool added = false;
        DangerousAddRef(ref added);
        try
        {
            uint identifier = unchecked((uint)handle);
            fixed (char* name = path)
            {
                char* file = name;
                ThrowIfFailed(RegisterResources(identifier, 1, &file, 0, null, 0, null), "RmRegisterResources");
            }

            const int maximumOwners = 32;
            DebuggerRestartManagerProcessInfo* processes = stackalloc DebuggerRestartManagerProcessInfo[maximumOwners];
            uint count = maximumOwners;
            uint result = GetList(identifier, out uint needed, ref count, processes, out _);
            ThrowIfFailed(result, $"RmGetList (required owners: {needed})");
            if (count > maximumOwners)
            {
                throw new InvalidDataException("Restart Manager returned more processes than the supplied buffer can hold.");
            }

            int[] owners = new int[count];
            for (int index = 0; index < owners.Length; index++)
            {
                owners[index] = checked((int)processes[index]._process._processId);
            }
            return owners;
        }
        finally
        {
            if (added)
            {
                DangerousRelease();
            }
        }
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => EndSession(unchecked((uint)handle)) == 0;

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != 0)
        {
            throw new Win32Exception(unchecked((int)result), $"{operation}: {new Win32Exception(unchecked((int)result)).Message}");
        }
    }

    [LibraryImport("Rstrtmgr.dll", EntryPoint = "RmStartSession")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint StartSession(out uint session, uint flags, char* key);

    [LibraryImport("Rstrtmgr.dll", EntryPoint = "RmRegisterResources")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint RegisterResources(uint session, uint fileCount, char** files,
        uint applicationCount, DebuggerRestartManagerProcess* applications, uint serviceCount, char** services);

    [LibraryImport("Rstrtmgr.dll", EntryPoint = "RmGetList")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint GetList(uint session, out uint needed, ref uint count,
        DebuggerRestartManagerProcessInfo* processes, out uint rebootReasons);

    [LibraryImport("Rstrtmgr.dll", EntryPoint = "RmEndSession")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint EndSession(uint session);
}
