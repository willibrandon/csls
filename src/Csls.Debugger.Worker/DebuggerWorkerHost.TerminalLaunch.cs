using Csls.Debugger.Control;

namespace Csls.Debugger.Worker;

/// <summary>
/// Runs a target inside an editor-owned terminal through the private launch channel.
/// </summary>
internal static partial class DebuggerWorkerHost
{
    private static Task<int> RunTerminalLaunchAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count != 1)
        {
            throw new InvalidDataException("The terminal launcher accepts no positional arguments.");
        }

        string pipeName = Environment.GetEnvironmentVariable(DebuggerTerminalLauncher.PipeEnvironmentVariable)
            ?? throw new InvalidDataException("The terminal launch pipe is unavailable.");
        string secret = Environment.GetEnvironmentVariable(DebuggerTerminalLauncher.SecretEnvironmentVariable)
            ?? throw new InvalidDataException("The terminal launch secret is unavailable.");
        Environment.SetEnvironmentVariable(DebuggerTerminalLauncher.PipeEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(DebuggerTerminalLauncher.SecretEnvironmentVariable, null);
        return DebuggerTerminalLauncher.RunAsync(pipeName, secret, cancellationToken);
    }
}
