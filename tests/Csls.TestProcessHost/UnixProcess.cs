using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>
/// Replaces the Unix test host with its configured child process.
/// </summary>
internal static partial class UnixProcess
{
    /// <summary>
    /// Executes a child in the current process so PTY ownership and signals remain exact.
    /// </summary>
    internal static void Execute(string fileName, IReadOnlyList<string> arguments)
    {
        nint fileNamePointer = Marshal.StringToCoTaskMemUTF8(fileName);
        nint argumentsPointer = Marshal.AllocHGlobal(
            checked((arguments.Count + 2) * nint.Size));
        nint[] argumentPointers = new nint[arguments.Count + 1];
        try
        {
            argumentPointers[0] = Marshal.StringToCoTaskMemUTF8(fileName);
            Marshal.WriteIntPtr(argumentsPointer, 0, argumentPointers[0]);
            for (int index = 0; index < arguments.Count; index++)
            {
                argumentPointers[index + 1] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                Marshal.WriteIntPtr(
                    argumentsPointer,
                    (index + 1) * nint.Size,
                    argumentPointers[index + 1]);
            }

            Marshal.WriteIntPtr(
                argumentsPointer,
                (arguments.Count + 1) * nint.Size,
                nint.Zero);
            _ = ExecuteCore(fileNamePointer, argumentsPointer);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            foreach (nint argumentPointer in argumentPointers)
            {
                Marshal.FreeCoTaskMem(argumentPointer);
            }

            Marshal.FreeHGlobal(argumentsPointer);
            Marshal.FreeCoTaskMem(fileNamePointer);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libc", EntryPoint = "execvp", SetLastError = true)]
    private static partial int ExecuteCore(nint fileName, nint arguments);
}
