using System.Diagnostics;

namespace Csls.Tests;

/// <summary>
/// Reads clipboard text from an isolated X display used by graphical editor tests.
/// </summary>
internal static class X11Clipboard
{
    /// <summary>
    /// Reads the current clipboard selection from the requested display.
    /// </summary>
    /// <param name="displayName">The isolated X display name.</param>
    /// <param name="cancellationToken">The read cancellation token.</param>
    /// <returns>The clipboard text, or an empty string when no owner provides text.</returns>
    internal static async Task<string> ReadTextAsync(
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var startInfo = new ProcessStartInfo
        {
            FileName = "xclip",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-selection");
        startInfo.ArgumentList.Add("clipboard");
        startInfo.ArgumentList.Add("-out");
        startInfo.Environment["DISPLAY"] = displayName;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("xclip did not start.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode == 0 || string.IsNullOrWhiteSpace(error))
        {
            return output;
        }

        if (error.Contains("target STRING not available", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        throw new InvalidOperationException(error.Trim());
    }
}
