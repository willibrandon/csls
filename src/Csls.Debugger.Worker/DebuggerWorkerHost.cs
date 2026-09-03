using Csls.Debugger.Terminal;
using System.Globalization;

namespace Csls.Debugger.Worker;

/// <summary>
/// Validates normalized launcher requests and runs the interactive debugger.
/// </summary>
internal static partial class DebuggerWorkerHost
{
    /// <summary>
    /// Executes one normalized debugger operation.
    /// </summary>
    /// <param name="arguments">The normalized launcher arguments.</param>
    /// <param name="cancellationToken">The worker cancellation token.</param>
    /// <returns>The debugger exit code.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        try
        {
            DebuggerWorkerEnvironment.InitializeCurrentProcess();
            return arguments.Count == 0
                ? Fail("The launcher supplied no debugger operation.")
                : arguments[0] switch
                {
                    "dap" => await RunDapAsync(cancellationToken).ConfigureAwait(false),
                    "doctor" => RunDoctor(),
                    "control" => await RunControlAsync(arguments, cancellationToken)
                        .ConfigureAwait(false),
                    "launch" => await RunLaunchAsync(arguments, cancellationToken)
                        .ConfigureAwait(false),
                    "attach" => await RunAttachAsync(arguments, cancellationToken)
                        .ConfigureAwait(false),
                    _ => Fail($"The launcher supplied an unknown debugger operation: {arguments[0]}")
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                BadImageFormatException or
                DllNotFoundException or
                EntryPointNotFoundException or
                IOException or
                InvalidDataException or
                InvalidOperationException or
                UnauthorizedAccessException)
        {
            return Fail(exception.Message);
        }
    }

    private static Task<int> RunLaunchAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count < 6 ||
            !int.TryParse(
                arguments[4],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int line))
        {
            throw new InvalidDataException(
                "The launcher supplied an invalid interactive launch request.");
        }

        return DebuggerTerminalHost.RunLaunchAsync(
            new DebuggerTerminalLaunchOptions
            {
                Program = arguments[1],
                WorkingDirectory = arguments[2],
                SourcePath = arguments[3],
                Line = line,
                RuntimeHostPath = string.IsNullOrEmpty(arguments[5]) ? null : arguments[5],
                Arguments = arguments.Skip(6).ToArray()
            },
            cancellationToken);
    }

    private static Task<int> RunAttachAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 2 ||
            !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int processId))
        {
            throw new InvalidDataException(
                "The launcher supplied an invalid interactive attach request.");
        }

        return DebuggerTerminalHost.RunAttachAsync(
            new DebuggerTerminalAttachOptions(processId),
            cancellationToken);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
