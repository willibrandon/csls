using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Reads cross-platform machine and .NET toolchain metadata without a command shell.
/// </summary>
internal static class PerformanceEnvironmentReader
{
    /// <summary>
    /// Reads the current machine and SDK metadata.
    /// </summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The complete performance environment.</returns>
    internal static async Task<PerformanceEnvironment> ReadAsync(
        CancellationToken cancellationToken)
    {
        return new PerformanceEnvironment
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Runtime = RuntimeInformation.FrameworkDescription,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            OperatingSystemArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ProcessorModel = await ReadProcessorModelAsync(cancellationToken)
                .ConfigureAwait(false),
            DotNetSdkVersion = await ReadDotNetSdkVersionAsync(cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private static async Task<string> ReadProcessorModelAsync(
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (string line in await File.ReadAllLinesAsync(
                    "/proc/cpuinfo",
                    cancellationToken).ConfigureAwait(false))
                {
                    int separator = line.IndexOf(':', StringComparison.Ordinal);
                    if (separator > 0 &&
                        line.AsSpan(0, separator).Trim().Equals(
                            "model name",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return line[(separator + 1)..].Trim();
                    }
                }
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or
                IOException or
                UnauthorizedAccessException)
            {
                return RuntimeInformation.ProcessArchitecture.ToString();
            }
        }

        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsProcessorModel();
        }

        if (OperatingSystem.IsMacOS())
        {
            string result = await RunForOutputAsync(
                "/usr/sbin/sysctl",
                ["-n", "machdep.cpu.brand_string"],
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static string ReadWindowsProcessorModel()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            writable: false);
        return key?.GetValue("ProcessorNameString") as string ??
            RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static Task<string> ReadDotNetSdkVersionAsync(
        CancellationToken cancellationToken)
    {
        string dotnetPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return RunForOutputAsync(dotnetPath, ["--version"], cancellationToken);
    }

    private static async Task<string> RunForOutputAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    $"The metadata process did not start: {executablePath}");
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = (await outputTask.ConfigureAwait(false)).Trim();
            string error = (await errorTask.ConfigureAwait(false)).Trim();
            return process.ExitCode == 0 ? output : error;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }
}
