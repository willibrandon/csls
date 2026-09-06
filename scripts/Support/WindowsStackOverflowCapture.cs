using System.Diagnostics;
using System.IO.Compression;

namespace Csls.Support;

/// <summary>
/// Captures native exception evidence from one isolated stack-overflow reproduction.
/// </summary>
internal static class WindowsStackOverflowCapture
{
    private static readonly string[] s_environmentVariables =
        ["SystemRoot", "WINDIR", "SystemDrive", "TEMP", "TMP", "PATH", "DOTNET_ROOT"];

    /// <summary>
    /// Runs the real fixture under the current Microsoft native crash collector with bounded lifetime.
    /// </summary>
    /// <param name="fixturePath">The compiled process-host assembly to execute.</param>
    /// <param name="outputPath">The parent directory for independently named capture artifacts.</param>
    /// <param name="cancellationToken">Cancels tool acquisition and the owned process tree.</param>
    /// <returns>Zero after capture completes, or one when capture fails.</returns>
    internal static async Task<int> RunAsync(string fixturePath, string outputPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native exception capture requires Windows.");
        }

        string fixture = Path.GetFullPath(fixturePath);
        if (!File.Exists(fixture))
        {
            throw new FileNotFoundException("The compiled stack-overflow fixture was not found.", fixture);
        }

        string directory = Path.Join(Path.GetFullPath(outputPath), $"native-stack-overflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        await Console.Out.WriteLineAsync($"Native crash evidence: {directory}").ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        string collector = await DownloadCollectorAsync(directory, deadline.Token).ConfigureAwait(false);
        var startInfo = new ProcessStartInfo(collector)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Crash artifacts must contain only the fixture's environment, not CI credentials.
        startInfo.Environment.Clear();
        foreach ((string name, string? value) in s_environmentVariables
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(pair => pair.Value is not null))
        {
            startInfo.Environment[name] = value;
        }

        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet.exe";
        foreach (string argument in new[]
        {
            "-accepteula", "-mm", "-g", "-e", "1", "-f", "C00000FD,C0000005", "-n", "3", "-t",
            "-x", directory, host, fixture, "--debugger-stack-overflow-fixture"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The native crash collector did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            string transcript = await output.ConfigureAwait(false) + Environment.NewLine + await error.ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(directory, "capture.log"), transcript, CancellationToken.None)
                .ConfigureAwait(false);
            // ProcDump redirects UTF-16 diagnostics into the same pipe as the fixture's ASCII marker.
            // Preserve the original transcript and make the fixed diagnostic text readable in CI.
            await Console.Out.WriteLineAsync(transcript.Replace("\0", string.Empty, StringComparison.Ordinal))
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"Native collector exit code: {process.ExitCode}").ConfigureAwait(false);
            File.Delete(collector);
        }

        // ProcDump returns two when the target exits before the maximum dump count is reached.
        return process.ExitCode is 0 or 2 && Directory.EnumerateFiles(directory, "*.dmp").Any() ? 0 : 1;
    }

    private static async Task<string> DownloadCollectorAsync(string directory, CancellationToken cancellationToken)
    {
        const int maximumArchiveBytes = 16 * 1024 * 1024;
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true };
        using var client = new HttpClient(handler);
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("https://download.sysinternals.com/files/Procdump.zip"),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var archiveBytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int length;
        while ((length = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (archiveBytes.Length + length > maximumArchiveBytes)
            {
                throw new InvalidDataException("The native collector archive exceeds the capture size budget.");
            }
            await archiveBytes.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }

        archiveBytes.Position = 0;
        using var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
        ZipArchiveEntry entry = archive.GetEntry("procdump64.exe")
            ?? throw new InvalidDataException("The native collector archive has no Windows x64 executable.");
        if (entry.Length > maximumArchiveBytes)
        {
            throw new InvalidDataException("The native collector executable exceeds the capture size budget.");
        }

        string path = Path.Join(directory, "procdump64.exe");
        await entry.ExtractToFileAsync(path, cancellationToken).ConfigureAwait(false);
        return path;
    }
}
