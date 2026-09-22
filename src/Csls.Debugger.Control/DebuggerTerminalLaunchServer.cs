using Csls.Debugger.Contracts;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace Csls.Debugger.Control;

/// <summary>
/// Authenticates one terminal launcher and retains its reported target process.
/// </summary>
public sealed class DebuggerTerminalLaunchServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(DebuggerTerminalLaunchProtocol.SecretBytes);
    private Process? _target;
    private int _accepted;
    private int _disposed;

    /// <summary>
    /// Creates a private, same-user launch endpoint before the terminal is opened.
    /// </summary>
    public DebuggerTerminalLaunchServer()
    {
        PipeName = $"{DebuggerTerminalLaunchProtocol.PipePrefix}{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    /// <summary>
    /// Gets the unique local pipe name passed only to the terminal launcher.
    /// </summary>
    public string PipeName { get; }

    /// <summary>
    /// Gets the one-use secret passed only to the terminal launcher.
    /// </summary>
    public string LaunchSecret => Convert.ToBase64String(_secret);

    /// <summary>
    /// Gets the retained target identity after the launcher reports its child.
    /// </summary>
    public int? TargetProcessId => _target?.Id;

    /// <summary>
    /// Authenticates the launcher, sends its invocation, and receives the actual child PID.
    /// </summary>
    /// <param name="instruction">The exact target invocation built by the debugger worker.</param>
    /// <param name="cancellationToken">Cancels launch and closes the pending connection.</param>
    /// <returns>The retained target process identifier.</returns>
    public async Task<int> AcceptAsync(
        DebuggerTerminalLaunchInstruction instruction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _accepted, 1) != 0)
        {
            throw new InvalidOperationException("The terminal launch endpoint already accepted a launcher.");
        }

        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        byte[] received = await DebuggerTerminalLaunchProtocol.ReadSecretAsync(_pipe, cancellationToken)
            .ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(received, _secret))
        {
            throw new UnauthorizedAccessException("The terminal launcher could not authenticate.");
        }

        await DebuggerTerminalLaunchProtocol.WriteInstructionAsync(_pipe, instruction, cancellationToken)
            .ConfigureAwait(false);
        int processId = await DebuggerTerminalLaunchProtocol.ReadIntegerAsync(_pipe, cancellationToken)
            .ConfigureAwait(false);
        if (processId <= 0)
        {
            throw new InvalidDataException("The terminal launcher did not report a valid target PID.");
        }

        _target = Process.GetProcessById(processId);
        if (_target.HasExited)
        {
            throw new InvalidOperationException("The terminal target exited during startup.");
        }

        return processId;
    }

    /// <summary>
    /// Receives the launcher-owned target's final exit code.
    /// </summary>
    /// <param name="cancellationToken">Cancels observation of the terminal session.</param>
    /// <returns>The target exit code reported by its direct parent.</returns>
    public async Task<int> ReadExitCodeAsync(CancellationToken cancellationToken)
    {
        if (_target is null)
        {
            throw new InvalidOperationException("The terminal target has not started.");
        }

        try
        {
            return await DebuggerTerminalLaunchProtocol.ReadIntegerAsync(_pipe, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            if (!_target.HasExited)
            {
                _target.Kill(entireProcessTree: false);
            }

            throw new IOException("The terminal launcher closed before reporting target exit.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_target is Process target)
            {
                using (target)
                {
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: false);
                    }

                    await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(_secret);
        }
    }
}
