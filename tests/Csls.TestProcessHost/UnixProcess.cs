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
    /// <param name="fileName">The executable path or name.</param>
    /// <param name="arguments">The exact child arguments.</param>
    /// <param name="environmentOverrides">Environment values applied before execution.</param>
    internal static void Execute(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environmentOverrides)
    {
        Dictionary<string, string> environment = CreateEnvironment(environmentOverrides);
        string executablePath = ResolveExecutable(fileName, environment);
        nint fileNamePointer = Marshal.StringToCoTaskMemUTF8(executablePath);
        nint argumentsPointer = Marshal.AllocHGlobal(
            checked((arguments.Count + 2) * nint.Size));
        nint[] argumentPointers = new nint[arguments.Count + 1];
        nint environmentPointer = Marshal.AllocHGlobal(
            checked((environment.Count + 1) * nint.Size));
        nint[] environmentPointers = new nint[environment.Count];
        try
        {
            argumentPointers[0] = Marshal.StringToCoTaskMemUTF8(executablePath);
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
            int environmentIndex = 0;
            foreach ((string name, string value) in environment)
            {
                environmentPointers[environmentIndex] = Marshal.StringToCoTaskMemUTF8(
                    $"{name}={value}");
                Marshal.WriteIntPtr(
                    environmentPointer,
                    environmentIndex * nint.Size,
                    environmentPointers[environmentIndex]);
                environmentIndex++;
            }

            Marshal.WriteIntPtr(
                environmentPointer,
                environment.Count * nint.Size,
                nint.Zero);
            _ = ExecuteCore(fileNamePointer, argumentsPointer, environmentPointer);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            foreach (nint argumentPointer in argumentPointers)
            {
                Marshal.FreeCoTaskMem(argumentPointer);
            }

            foreach (nint environmentValuePointer in environmentPointers)
            {
                Marshal.FreeCoTaskMem(environmentValuePointer);
            }

            Marshal.FreeHGlobal(environmentPointer);
            Marshal.FreeHGlobal(argumentsPointer);
            Marshal.FreeCoTaskMem(fileNamePointer);
        }
    }

    private static Dictionary<string, string> CreateEnvironment(
        IReadOnlyDictionary<string, string> overrides)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach ((string name, string value) in overrides)
        {
            environment[name] = value;
        }

        return environment;
    }

    private static string ResolveExecutable(
        string fileName,
        Dictionary<string, string> environment)
    {
        if (Path.IsPathFullyQualified(fileName) ||
            fileName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return fileName;
        }

        if (environment.TryGetValue("PATH", out string? searchPath))
        {
            foreach (string directoryPath in searchPath.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directoryPath))
                {
                    continue;
                }

                string candidatePath = Path.Join(directoryPath, fileName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        throw new FileNotFoundException($"The hosted executable was not found: {fileName}");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static partial int ExecuteCore(
        nint fileName,
        nint arguments,
        nint environment);
}
