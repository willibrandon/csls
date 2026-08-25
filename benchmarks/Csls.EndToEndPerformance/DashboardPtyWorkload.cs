using Hex1b;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Connects a real dashboard process to the Hex1b terminal emulator through a PTY.
/// </summary>
internal sealed class DashboardPtyWorkload : IHex1bTerminalWorkloadAdapter
{
    private readonly Hex1bTerminalChildProcess _process;

    /// <summary>
    /// Creates a real dashboard process workload.
    /// </summary>
    /// <param name="fileName">The executable path.</param>
    /// <param name="arguments">The exact process arguments.</param>
    /// <param name="workingDirectory">The process working directory.</param>
    /// <param name="width">The initial terminal width.</param>
    /// <param name="height">The initial terminal height.</param>
    /// <param name="environment">Environment variables added to the inherited environment.</param>
    internal DashboardPtyWorkload(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int width,
        int height,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);
        string processFileName = fileName;
        string[] processArguments = [.. arguments];
        Dictionary<string, string>? childEnvironment = new(
            environment,
            StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            processFileName = "env";
            processArguments =
            [
                .. childEnvironment
                    .OrderBy(static item => item.Key, StringComparer.Ordinal)
                    .Select(static item => string.Concat(item.Key, "=", item.Value)),
                fileName,
                .. arguments
            ];
            childEnvironment = null;
        }

        _process = new Hex1bTerminalChildProcess(
            processFileName,
            processArguments,
            workingDirectory,
            childEnvironment,
            inheritEnvironment: true,
            width,
            height);
    }

    /// <inheritdoc />
    public event Action? Disconnected
    {
        add => _process.Disconnected += value;
        remove => _process.Disconnected -= value;
    }

    /// <summary>
    /// Runs the child process, terminal pumps, and dashboard interaction together.
    /// </summary>
    /// <param name="terminal">The terminal built over this workload.</param>
    /// <param name="interaction">The dashboard interaction to execute.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The child process exit code.</returns>
    internal async Task<int> RunAsync(
        Hex1bTerminal terminal,
        Func<Task> interaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(interaction);
        await _process.StartAsync(cancellationToken).ConfigureAwait(false);
        Task<int> processTask = _process.WaitForExitAsync(cancellationToken);
        Task<int> terminalTask = terminal.RunAsync(cancellationToken);
        try
        {
            await interaction().ConfigureAwait(false);
            await Task.WhenAll(processTask, terminalTask).ConfigureAwait(false);
        }
        catch
        {
            _process.Kill();
            throw;
        }

        return await processTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(
        CancellationToken cancellationToken = default) =>
        _process.ReadOutputAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default) =>
        _process.WriteInputAsync(data, cancellationToken);

    /// <inheritdoc />
    public ValueTask ResizeAsync(
        int width,
        int height,
        CancellationToken cancellationToken = default) =>
        _process.ResizeAsync(width, height, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _process.DisposeAsync();
}
