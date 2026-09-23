using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Projects Mach task, thread, and register inspection with explicit native ownership.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacThreadContextNativeMethods
{
    private const string Library = "/usr/lib/libSystem.B.dylib";

    /// <summary>
    /// Gets the borrowed task port for the current debugger process.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "mach_task_self")]
    internal static partial uint GetCurrentTask();

    /// <summary>
    /// Acquires an owned task send right for the selected process.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "task_for_pid")]
    internal static partial int OpenTask(uint currentTask, int processId, out uint task);

    /// <summary>
    /// Acquires an allocated array of owned thread send rights.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "task_threads")]
    internal static partial int GetThreads(uint task, out nint threads, out uint count);

    /// <summary>
    /// Reads public thread information into a caller-owned word buffer.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "thread_info")]
    internal static partial int GetThreadInfo(uint thread, int flavor, uint* words, ref uint count);

    /// <summary>
    /// Reads public thread register state into a caller-owned word buffer.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "thread_get_state")]
    internal static partial int GetThreadState(uint thread, int flavor, uint* words, ref uint count);

    /// <summary>
    /// Releases one owned Mach send right in the current task.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "mach_port_deallocate")]
    internal static partial int ReleasePort(uint currentTask, uint port);

    /// <summary>
    /// Releases the Mach-allocated thread-port array in the current task.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "mach_vm_deallocate")]
    internal static partial int ReleaseMemory(uint currentTask, ulong address, ulong size);
}
