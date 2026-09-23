using Csls.Control;
using Csls.Debugger.Contracts;
using StreamJsonRpc;
using System.Diagnostics;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Owns one supervised managed evaluator process and private RPC connection.
/// </summary>
internal sealed partial class DebuggerEvaluatorClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 4 * 1024 * 1024;
    private readonly Process _process;
    private readonly ValueTask<string> _diagnostics;
    private readonly BoundedMessageStream _sending;
    private readonly BoundedMessageStream _receiving;
    private readonly NerdbankMessagePackFormatter _formatter;
    private readonly LengthHeaderMessageHandler _handler;
    private readonly JsonRpc _rpc;
    private string _capturedDiagnostics = string.Empty;
    private int _disposed;

    private DebuggerEvaluatorClient(
        Process process,
        ValueTask<string> diagnostics,
        BoundedMessageStream sending,
        BoundedMessageStream receiving,
        NerdbankMessagePackFormatter formatter,
        LengthHeaderMessageHandler handler,
        JsonRpc rpc)
    {
        _process = process;
        _diagnostics = diagnostics;
        _sending = sending;
        _receiving = receiving;
        _formatter = formatter;
        _handler = handler;
        _rpc = rpc;
    }

    /// <summary>
    /// Starts and verifies one evaluator worker over anonymous standard-stream pipes.
    /// </summary>
    /// <param name="cancellationToken">Cancels worker startup and negotiation.</param>
    /// <returns>The connected owning evaluator client.</returns>
    internal static async Task<DebuggerEvaluatorClient> StartAsync(
        CancellationToken cancellationToken)
    {
        string workerPath = DebuggerEvaluatorWorkerEnvironment.ResolveCurrentWorker();
        bool managed = string.Equals(
            Path.GetExtension(workerPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = managed ? ResolveDotNetHost() : workerPath,
            WorkingDirectory = Path.GetDirectoryName(workerPath)
                ?? throw new InvalidOperationException(
                    $"Evaluator worker {workerPath} has no containing directory."),
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (managed)
        {
            startInfo.ArgumentList.Add(workerPath);
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The debugger evaluator worker did not start.");
        ValueTask<string> diagnostics = DebuggerEvaluatorDiagnostics.DrainAsync(
            process.StandardError);
        var sending = new BoundedMessageStream(
            process.StandardInput.BaseStream,
            MaximumMessageBytes,
            leaveOpen: true);
        var receiving = new BoundedMessageStream(
            process.StandardOutput.BaseStream,
            MaximumMessageBytes,
            leaveOpen: true);
        NerdbankMessagePackFormatter formatter = DebuggerEvaluatorRpcFormatter.Create();
        var handler = new LengthHeaderMessageHandler(sending, receiving, formatter);
        var rpc = new JsonRpc(handler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "debugger-evaluator-client"
        };
        var client = new DebuggerEvaluatorClient(
            process,
            diagnostics,
            sending,
            receiving,
            formatter,
            handler,
            rpc);
        try
        {
            rpc.StartListening();
            int version = await rpc.InvokeWithCancellationAsync<int>(
                DebuggerEvaluatorMethods.GetProtocolVersion,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (version != DebuggerEvaluatorProtocol.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Evaluator protocol {version} is incompatible with " +
                    $"{DebuggerEvaluatorProtocol.CurrentVersion}.");
            }

            return client;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            string detail = string.IsNullOrWhiteSpace(client._capturedDiagnostics)
                ? string.Empty
                : $" Evaluator diagnostics: {client._capturedDiagnostics.Trim()}";
            throw new IOException(
                $"The managed debugger evaluator failed during startup.{detail}",
                exception);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Compiles source syntax into a language-neutral evaluator plan.
    /// </summary>
    /// <param name="request">The selected language and expression.</param>
    /// <param name="cancellationToken">Cancels expression binding.</param>
    /// <returns>The validated language-neutral plan.</returns>
    internal async Task<DebugExpressionPlan> CompileAsync(
        DebugExpressionCompileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            return await _rpc.InvokeWithParameterObjectAsync<DebugExpressionPlan>(
                DebuggerEvaluatorMethods.Compile,
                NamedArgs.Create(request),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteInvocationException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
        catch (ConnectionLostException exception)
        {
            throw new IOException(
                "The managed debugger evaluator process disconnected unexpectedly.",
                exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _rpc.Dispose();
        await _handler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        await _sending.DisposeAsync().ConfigureAwait(false);
        await _receiving.DisposeAsync().ConfigureAwait(false);
        _process.StandardInput.Close();
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _capturedDiagnostics = await _diagnostics.ConfigureAwait(false);
        _process.Dispose();
    }

    private static string ResolveDotNetHost()
    {
        string? path = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(path) ? "dotnet" : path;
    }
}
