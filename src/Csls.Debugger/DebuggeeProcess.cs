using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Owns one debugger-launched process and its redirected streams.
/// </summary>
internal sealed class DebuggeeProcess : IDebuggeeProcess
{
    private readonly Process _process;
    private int _detached;
    private int _disposed;

    private DebuggeeProcess(Process process)
    {
        _process = process;
    }

    /// <summary>
    /// Gets the operating-system process identifier.
    /// </summary>
    public int Id => _process.Id;

    /// <summary>
    /// Gets the display name of the launched program.
    /// </summary>
    public string Name => _process.ProcessName;

    /// <inheritdoc />
    public bool OwnsProcess => true;

    /// <summary>
    /// Starts a target without invoking a command shell.
    /// </summary>
    /// <param name="options">The validated launch options.</param>
    /// <returns>The owned target process.</returns>
    internal static DebuggeeProcess Start(DebuggeeLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool managedAssembly = string.Equals(
            Path.GetExtension(options.Program),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        string executable = managedAssembly
            ? options.RuntimeHostPath ?? "dotnet"
            : options.Program;
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (managedAssembly)
        {
            startInfo.ArgumentList.Add(options.Program);
        }

        foreach (string argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in options.Environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"The operating system did not start '{options.Program}'.");
            }

            return new DebuggeeProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies target standard output to a debugger callback until end of stream.
    /// </summary>
    /// <param name="writeAsync">Receives each output segment.</param>
    /// <param name="cancellationToken">Cancels output collection.</param>
    /// <returns>A task that completes when the stream closes.</returns>
    public Task CopyStandardOutputAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken) =>
        CopyAsync(_process.StandardOutput, writeAsync, cancellationToken);

    /// <summary>
    /// Copies target standard error to a debugger callback until end of stream.
    /// </summary>
    /// <param name="writeAsync">Receives each output segment.</param>
    /// <param name="cancellationToken">Cancels output collection.</param>
    /// <returns>A task that completes when the stream closes.</returns>
    public Task CopyStandardErrorAsync(
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken) =>
        CopyAsync(_process.StandardError, writeAsync, cancellationToken);

    /// <summary>
    /// Waits for the target and returns its exit code.
    /// </summary>
    /// <param name="cancellationToken">Cancels only the wait operation.</param>
    /// <returns>The target exit code.</returns>
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>
    /// Terminates the target and its descendants when it is still running.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for process exit.</param>
    /// <returns>A task that completes after the target exits.</returns>
    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        if (_process.HasExited)
        {
            return;
        }

        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Detach()
    {
        Volatile.Write(ref _detached, 1);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _detached) == 0 && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }

    private static async Task CopyAsync(
        TextReader reader,
        Func<string, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await reader
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            await writeAsync(new string(buffer, 0, count), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
