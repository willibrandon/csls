using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Exercises native runtime status consumers after the debugger's owner reaps a real child.
/// </summary>
internal static partial class UnixWaitStatusFixture
{
    /// <summary>
    /// Reports the owner and runtime observations of one directly spawned child's exit.
    /// </summary>
    /// <param name="exitCode">The child's requested Unix exit code.</param>
    /// <returns>Zero after reporting all native observations.</returns>
    internal static int Run(int exitCode)
    {
        Initialize();
        int processId = SpawnChild(exitCode);
        try
        {
            Track(processId);
            int ownerResult = WaitForChild(processId, out int status);
            int consumerResult = WaitRuntime(processId, out int consumerExitCode, out int signal);
            int error = Marshal.GetLastPInvokeError();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{processId},{ownerResult},{status},{consumerResult},{consumerExitCode},{signal},{error}"));
            return 0;
        }
        finally
        {
            // The child exits without external input. Reap it if an earlier operation failed.
            _ = WaitForChild(processId, out _);
        }
    }

    private static int WaitForChild(int processId, out int status)
    {
        int result;
        do
        {
            result = WaitOwner(processId, out status, 0);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);

        return result;
    }

    private static unsafe int SpawnChild(int exitCode)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The fixture has no executable path.");
        string[] arguments =
        [
            executable,
            typeof(UnixWaitStatusFixture).Assembly.Location,
            "--print-environment-and-exit",
            "CSLS_UNIX_WAIT_EMPTY_VALUE",
            exitCode.ToString(CultureInfo.InvariantCulture)
        ];
        nint[] argumentPointers = new nint[arguments.Length + 1];
        try
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                argumentPointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            }

            nint emptyEnvironment = nint.Zero;
            fixed (nint* argv = argumentPointers)
            {
                int error = Spawn(out int processId, argumentPointers[0], nint.Zero, nint.Zero, argv, &emptyEnvironment);
                if (error != 0)
                {
                    throw new Win32Exception(error);
                }

                return processId;
            }
        }
        finally
        {
            foreach (nint argument in argumentPointers)
            {
                Marshal.FreeCoTaskMem(argument);
            }
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_initialize")]
    private static partial void Initialize();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_track")]
    private static partial void Track(int processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("Csls.Debugger.UnixWait", EntryPoint = "csls_waitpid_wait", SetLastError = true)]
    private static partial int WaitOwner(int processId, out int status, int options);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("System.Native", EntryPoint = "SystemNative_WaitPidExitedNoHang", SetLastError = true)]
    private static partial int WaitRuntime(int processId, out int exitCode, out int terminatingSignal);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "posix_spawn")]
    private static unsafe partial int Spawn(
        out int processId,
        nint path,
        nint fileActions,
        nint attributes,
        nint* arguments,
        nint* environment);
}
