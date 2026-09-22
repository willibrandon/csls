using Csls.Debugger.Contracts;
using System.Buffers.Binary;
using System.Text.Json;

namespace Csls.Debugger.Control;

/// <summary>
/// Exchanges bounded one-shot launch messages over a private local pipe.
/// </summary>
internal static class DebuggerTerminalLaunchProtocol
{
    /// <summary>
    /// Gets the fixed prefix for one debugger-owned terminal pipe.
    /// </summary>
    internal const string PipePrefix = "csls-debug-terminal-";

    /// <summary>
    /// Gets the fixed length of an authenticated terminal launch secret.
    /// </summary>
    internal const int SecretBytes = 32;
    private const int MaximumInstructionBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Writes the launch secret before either peer exchanges target details.
    /// </summary>
    /// <param name="stream">The authenticated launch channel.</param>
    /// <param name="secret">The one-use secret to send.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes after the secret is flushed.</returns>
    internal static async Task WriteSecretAsync(Stream stream, byte[] secret, CancellationToken cancellationToken)
    {
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException("The terminal launch secret has an invalid length.", nameof(secret));
        }

        await stream.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the exact launch secret presented by the connecting peer.
    /// </summary>
    /// <param name="stream">The connecting peer's launch channel.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The received fixed-length secret.</returns>
    internal static async Task<byte[]> ReadSecretAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] secret = new byte[SecretBytes];
        await stream.ReadExactlyAsync(secret, cancellationToken).ConfigureAwait(false);
        return secret;
    }

    /// <summary>
    /// Writes one bounded target invocation after authenticating the launcher.
    /// </summary>
    /// <param name="stream">The authenticated launch channel.</param>
    /// <param name="instruction">The exact target invocation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes after the instruction is flushed.</returns>
    internal static async Task WriteInstructionAsync(
        Stream stream, DebuggerTerminalLaunchInstruction instruction, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            instruction, DebuggerTerminalLaunchJsonContext.Default.DebuggerTerminalLaunchInstruction);
        if (payload.Length is 0 or > MaximumInstructionBytes)
        {
            throw new InvalidDataException("The terminal launch instruction exceeds its size limit.");
        }

        await WriteIntegerAsync(stream, payload.Length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one bounded target invocation after authentication.
    /// </summary>
    /// <param name="stream">The authenticated launch channel.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The complete target invocation.</returns>
    internal static async Task<DebuggerTerminalLaunchInstruction> ReadInstructionAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        int length = await ReadIntegerAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length is <= 0 or > MaximumInstructionBytes)
        {
            throw new InvalidDataException("The terminal launch instruction has an invalid size.");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(
                payload, DebuggerTerminalLaunchJsonContext.Default.DebuggerTerminalLaunchInstruction)
                ?? throw new InvalidDataException("The terminal launch instruction is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The terminal launch instruction is invalid JSON.", exception);
        }
    }

    /// <summary>
    /// Writes one process identifier or exit code in a fixed-width frame.
    /// </summary>
    /// <param name="stream">The authenticated launch channel.</param>
    /// <param name="value">The process identifier or exit code.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes after the value is flushed.</returns>
    internal static async Task WriteIntegerAsync(Stream stream, int value, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, value);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one process identifier or exit code from a fixed-width frame.
    /// </summary>
    /// <param name="stream">The authenticated launch channel.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The process identifier or exit code.</returns>
    internal static async Task<int> ReadIntegerAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(frame);
    }
}
