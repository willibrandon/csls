using Hex1b;
using System.Buffers;

namespace Csls.Tests;

/// <summary>
/// Runs a real PTY process while normalizing editor-specific CSI private sequences.
/// </summary>
internal sealed class Hex1bPtyWorkload : IHex1bTerminalWorkloadAdapter
{
    private readonly Hex1bTerminalChildProcess _process;
    private byte[] _pendingOutput = [];

    /// <summary>
    /// Creates an isolated PTY workload for a real editor process.
    /// </summary>
    /// <param name="fileName">The executable path.</param>
    /// <param name="arguments">The exact process arguments.</param>
    /// <param name="workingDirectory">The isolated process working directory.</param>
    /// <param name="width">The initial terminal width.</param>
    /// <param name="height">The initial terminal height.</param>
    /// <param name="environment">Optional environment variables added to the inherited process environment.</param>
    internal Hex1bPtyWorkload(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int width,
        int height,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        string processFileName = fileName;
        string[] processArguments = [.. arguments];
        Dictionary<string, string>? childEnvironment = environment is null
            ? null
            : new Dictionary<string, string>(environment, StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows() && childEnvironment is { Count: > 0 })
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
    /// Runs the PTY child, Hex1b terminal pumps, and editor interaction as one lifecycle.
    /// </summary>
    /// <param name="terminal">The terminal built over this workload.</param>
    /// <param name="interaction">The real editor interaction to perform while the pumps run.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The real child process exit code.</returns>
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
    public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(
        CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> output = await _process
            .ReadOutputAsync(cancellationToken)
            .ConfigureAwait(false);
        return NormalizeOutput(output);
    }

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

    private ReadOnlyMemory<byte> NormalizeOutput(ReadOnlyMemory<byte> output)
    {
        if (output.IsEmpty)
        {
            return output;
        }

        byte[] combined = new byte[_pendingOutput.Length + output.Length];
        _pendingOutput.CopyTo(combined, 0);
        output.CopyTo(combined.AsMemory(_pendingOutput.Length));
        _pendingOutput = [];

        var normalized = new ArrayBufferWriter<byte>(combined.Length);
        ReadOnlySpan<byte> bytes = combined;
        int copyStart = 0;
        int index = 0;
        while (index < bytes.Length)
        {
            if (bytes[index] != 0x1b)
            {
                index++;
                continue;
            }

            if (index + 1 >= bytes.Length)
            {
                normalized.Write(bytes[copyStart..index]);
                _pendingOutput = bytes[index..].ToArray();
                return normalized.WrittenMemory;
            }

            if (bytes[index + 1] != (byte)'[')
            {
                index += 2;
                continue;
            }

            int finalIndex = FindCsiFinalByte(bytes, index + 2);
            if (finalIndex < 0)
            {
                normalized.Write(bytes[copyStart..index]);
                _pendingOutput = bytes[index..].ToArray();
                return normalized.WrittenMemory;
            }

            if (index + 2 < bytes.Length &&
                bytes[index + 2] == (byte)'<' &&
                !IsSgrMouseSequence(bytes[index..(finalIndex + 1)]))
            {
                normalized.Write(bytes[copyStart..index]);
                copyStart = finalIndex + 1;
            }

            index = finalIndex + 1;
        }

        normalized.Write(bytes[copyStart..]);
        return normalized.WrittenMemory;
    }

    private static int FindCsiFinalByte(ReadOnlySpan<byte> bytes, int start)
    {
        for (int index = start; index < bytes.Length; index++)
        {
            if (bytes[index] is >= 0x40 and <= 0x7e)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSgrMouseSequence(ReadOnlySpan<byte> sequence)
    {
        if (sequence.Length < 9 ||
            sequence[0] != 0x1b ||
            sequence[1] != (byte)'[' ||
            sequence[2] != (byte)'<' ||
            sequence[^1] != (byte)'M' &&
            sequence[^1] != (byte)'m')
        {
            return false;
        }

        int separators = 0;
        bool hasDigit = false;
        foreach (byte value in sequence[3..^1])
        {
            if (value is >= (byte)'0' and <= (byte)'9')
            {
                hasDigit = true;
                continue;
            }

            if (value != (byte)';' || !hasDigit || separators == 2)
            {
                return false;
            }

            separators++;
            hasDigit = false;
        }

        return separators == 2 && hasDigit;
    }
}
