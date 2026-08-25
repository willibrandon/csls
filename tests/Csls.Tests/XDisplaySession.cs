using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Owns a reserved Xvfb display for one real graphical editor test workload.
/// </summary>
internal sealed class XDisplaySession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly FileStream _reservation;
    private readonly string _reservationPath;

    private XDisplaySession(
        Process process,
        FileStream reservation,
        string reservationPath,
        string displayName)
    {
        _process = process;
        _reservation = reservation;
        _reservationPath = reservationPath;
        DisplayName = displayName;
    }

    /// <summary>
    /// Gets the X display name supplied to graphical child processes.
    /// </summary>
    internal string DisplayName { get; }

    /// <summary>
    /// Starts Xvfb on an exclusively reserved display and waits for its readiness signal.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The ready isolated display session.</returns>
    internal static async Task<XDisplaySession> StartAsync(
        CancellationToken cancellationToken)
    {
        FileStream reservation = ReserveDisplay(out int displayNumber, out string reservationPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "Xvfb",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in new[]
        {
            $":{displayNumber.ToString(CultureInfo.InvariantCulture)}",
            "-displayfd",
            "1",
            "-screen",
            "0",
            "1280x800x24",
            "-nolisten",
            "tcp",
            "-ac"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Xvfb did not start.");
        }
        catch
        {
            await reservation.DisposeAsync().ConfigureAwait(false);
            File.Delete(reservationPath);
            throw;
        }

        var standardError = new StringBuilder();
        object standardErrorSync = new();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            lock (standardErrorSync)
            {
                _ = standardError.AppendLine(eventArgs.Data);
            }
        };
        process.BeginErrorReadLine();
        try
        {
            string? publishedDisplay = await process.StandardOutput.ReadLineAsync(
                cancellationToken).ConfigureAwait(false);
            string expectedDisplay = displayNumber.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(publishedDisplay, expectedDisplay, StringComparison.Ordinal))
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }

                string error;
                lock (standardErrorSync)
                {
                    error = standardError.ToString();
                }

                throw new InvalidOperationException(
                    $"Xvfb did not publish display {expectedDisplay}: {error}".Trim());
            }

            return new XDisplaySession(
                process,
                reservation,
                reservationPath,
                $":{expectedDisplay}");
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
            await reservation.DisposeAsync().ConfigureAwait(false);
            File.Delete(reservationPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        _process.Dispose();
        await _reservation.DisposeAsync().ConfigureAwait(false);
        File.Delete(_reservationPath);
    }

    private static FileStream ReserveDisplay(
        out int displayNumber,
        out string reservationPath)
    {
        for (int candidate = 90; candidate < 190; candidate++)
        {
            if (File.Exists($"/tmp/.X{candidate}-lock") ||
                File.Exists($"/tmp/.X11-unix/X{candidate}"))
            {
                continue;
            }

            string candidatePath = Path.Join(
                Path.GetTempPath(),
                $"csls-x-display-{candidate}.lock");
            try
            {
                FileStream reservation = new(
                    candidatePath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
                displayNumber = candidate;
                reservationPath = candidatePath;
                return reservation;
            }
            catch (IOException)
            {
                continue;
            }
        }

        throw new InvalidOperationException("No isolated X display number is available.");
    }
}
