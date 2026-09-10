using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Dump.Worker;

/// <summary>
/// Hosts one isolated managed process-dump debugger control connection.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !string.Equals(args[0], "control", StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync(
                "Usage: csls-debugger-dump-worker control").ConfigureAwait(false);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        Stream input = Console.OpenStandardInput();
        await using ConfiguredAsyncDisposable inputCleanup = input.ConfigureAwait(false);
        Stream output = Console.OpenStandardOutput();
        await using ConfiguredAsyncDisposable outputCleanup = output.ConfigureAwait(false);
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        try
        {
            await DebuggerRpcStreamServer.RunAsync(
                input,
                output,
                service,
                shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                BadImageFormatException or
                FileNotFoundException or
                IOException or
                InvalidDataException or
                InvalidOperationException or
                UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }
}
