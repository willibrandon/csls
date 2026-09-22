using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace Csls.Debugger.Control;

/// <summary>
/// Starts one debugger-owned target with its terminal's inherited standard handles.
/// </summary>
public static class DebuggerTerminalLauncher
{
    private const int ConnectionTimeoutMilliseconds = 5000;

    /// <summary>
    /// Gets the environment variable containing the private pipe name.
    /// </summary>
    public const string PipeEnvironmentVariable = "CSLS_TERMINAL_LAUNCH_PIPE";

    /// <summary>
    /// Gets the environment variable containing the one-use launch secret.
    /// </summary>
    public const string SecretEnvironmentVariable = "CSLS_TERMINAL_LAUNCH_SECRET";

    /// <summary>
    /// Connects to the owning debugger worker and runs its exact target invocation.
    /// </summary>
    /// <param name="pipeName">The private local endpoint created for this launch.</param>
    /// <param name="launchSecret">The one-use secret supplied to the terminal process.</param>
    /// <param name="cancellationToken">Cancels the launcher and its child.</param>
    /// <returns>The target exit code.</returns>
    public static async Task<int> RunAsync(
        string pipeName, string launchSecret, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchSecret);
        if (!pipeName.StartsWith(DebuggerTerminalLaunchProtocol.PipePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName.AsSpan(DebuggerTerminalLaunchProtocol.PipePrefix.Length), "N", out _))
        {
            throw new InvalidDataException("The terminal launch pipe name is invalid.");
        }

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(launchSecret);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The terminal launch secret is invalid.", exception);
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            try
            {
                await pipe.ConnectAsync(ConnectionTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new IOException("The debugger worker did not accept the terminal launch.", exception);
            }

            await DebuggerTerminalLaunchProtocol.WriteSecretAsync(pipe, secret, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
        DebuggerTerminalLaunchInstruction instruction = await DebuggerTerminalLaunchProtocol
            .ReadInstructionAsync(pipe, cancellationToken).ConfigureAwait(false);
        ProcessStartInfo start = CreateStartInfo(instruction);
        using Process target = Process.Start(start) ??
            throw new InvalidOperationException("The terminal target did not start.");
        try
        {
            await DebuggerTerminalLaunchProtocol.WriteIntegerAsync(pipe, target.Id, cancellationToken)
                .ConfigureAwait(false);
            using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            byte[] unexpectedMessage = new byte[1];
            Task<int> pipeClosed = pipe.ReadAsync(unexpectedMessage, observationCancellation.Token).AsTask();
            Task targetExited = target.WaitForExitAsync(cancellationToken);
            Task completed = await Task.WhenAny(pipeClosed, targetExited).ConfigureAwait(false);
            if (completed == pipeClosed)
            {
                int count = await pipeClosed.ConfigureAwait(false);
                if (count != 0 || !target.HasExited)
                {
                    throw new IOException("The owning debugger worker closed the terminal launch channel.");
                }
            }

            await targetExited.ConfigureAwait(false);
            await observationCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await pipeClosed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (observationCancellation.IsCancellationRequested)
            {
                Debug.Assert(pipeClosed.IsCanceled);
            }

            int exitCode = target.ExitCode;
            await DebuggerTerminalLaunchProtocol.WriteIntegerAsync(pipe, exitCode, cancellationToken)
                .ConfigureAwait(false);
            return exitCode;
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: false);
            }

            await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateStartInfo(DebuggerTerminalLaunchInstruction instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction.Program) ||
            !Path.IsPathFullyQualified(instruction.Program) ||
            !File.Exists(instruction.Program) ||
            string.IsNullOrWhiteSpace(instruction.WorkingDirectory) ||
            !Path.IsPathFullyQualified(instruction.WorkingDirectory) ||
            !Directory.Exists(instruction.WorkingDirectory) ||
            instruction.Arguments is null || instruction.Arguments.Count > 4096 ||
            instruction.Environment is null || instruction.Environment.Count > 8192)
        {
            throw new InvalidDataException("The terminal launch instruction has an invalid target.");
        }

        bool managedAssembly = string.Equals(Path.GetExtension(instruction.Program), ".dll",
            StringComparison.OrdinalIgnoreCase);
        string executable = managedAssembly
            ? instruction.RuntimeHostPath ?? "dotnet"
            : instruction.Program;
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("The terminal launch instruction has an invalid runtime host.");
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = instruction.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
        if (managedAssembly)
        {
            start.ArgumentList.Add(instruction.Program);
        }

        foreach (string argument in instruction.Arguments)
        {
            if (argument is null || argument.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException("The terminal launch instruction has an invalid argument.");
            }

            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach ((string name, string value) in instruction.Environment)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=', StringComparison.Ordinal) ||
                name.Contains('\0', StringComparison.Ordinal) || value is null ||
                value.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException("The terminal launch instruction has an invalid environment entry.");
            }

            start.Environment[name] = value;
        }

        if (instruction.SuspendForDebugging)
        {
            start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
        }

        return start;
    }
}
