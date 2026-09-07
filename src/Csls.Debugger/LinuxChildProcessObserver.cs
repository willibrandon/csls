using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Serializes child reaping and register-capture stops on one Linux wait owner.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxChildProcessObserver
{
    private const int InterruptedError = 4;
    private const int NoChildProcessError = 10;
    private readonly object _gate = new();
    private readonly Queue<LinuxThreadContextRequest> _requests = new();
    private bool _wakePending;
    private bool _finished;

    /// <summary>
    /// Starts child observation and completes a nonblocking identity preflight before returning.
    /// </summary>
    /// <param name="processId">The directly launched native child.</param>
    internal LinuxChildProcessObserver(int processId)
    {
        using var ownershipReady = new ManualResetEventSlim();
        ExitCode = Task.Factory.StartNew(() => Observe(processId, ownershipReady), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        ownershipReady.Wait();
    }

    /// <summary>
    /// Gets the exact terminal child status or the native observation failure.
    /// </summary>
    internal Task<int?> ExitCode { get; }

    /// <summary>
    /// Captures registers while temporarily lending the child owner's wait to the inspector.
    /// </summary>
    /// <param name="threadId">The selected child thread.</param>
    /// <param name="cancellationToken">Cancels the queued or stopped native operation.</param>
    /// <returns>The captured register image after tracing is released.</returns>
    internal byte[] ReadRegisters(int threadId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new LinuxThreadContextRequest(threadId, cancellationToken);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            if (_requests.Count == 64)
            {
                throw new InvalidOperationException("The native register inspection queue is full.");
            }

            _requests.Enqueue(request);
            _wakePending = true;
            Monitor.PulseAll(_gate);
        }

        return request.WaitForCompletion();
    }

    private int? Observe(int processId, ManualResetEventSlim ownershipReady)
    {
        bool ready = false;
        try
        {
            using var notification = PosixSignalRegistration.Create(PosixSignal.SIGCHLD, _ => Wake());
            while (true)
            {
                lock (_gate)
                {
                    _wakePending = false;
                }

                int result = UnixWaitStatusInterposer.WaitProcess(processId, out int status, 1);
                int error = Marshal.GetLastPInvokeError();
                if (result == processId)
                {
                    int signal = status & 0x7f;
                    if (signal == 0x7f)
                    {
                        throw new InvalidOperationException("A native thread stop reached the child-exit observer without an owning inspection.");
                    }

                    return signal == 0 ? (status >> 8) & 0xff : 128 + signal;
                }

                if (result < 0 && error == InterruptedError)
                {
                    continue;
                }

                if (result < 0 && error == NoChildProcessError)
                {
                    return UnixWaitStatusInterposer.TryGetExitCode(processId, out int exitCode) ? exitCode : null;
                }

                if (result < 0)
                {
                    throw new Win32Exception(error, $"waitpid({processId}) failed.");
                }

                if (!ready)
                {
                    ready = true;
                    ownershipReady.Set();
                }

                LinuxThreadContextRequest? request;
                lock (_gate)
                {
                    _requests.TryDequeue(out request);
                    if (request is null && !_wakePending)
                    {
                        Monitor.Wait(_gate);
                    }
                }

                if (request is not null)
                {
                    request.Execute();
                    continue;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _finished = true;
                while (_requests.TryDequeue(out LinuxThreadContextRequest? request))
                {
                    request.TargetExited();
                }
            }

            if (!ready)
            {
                ownershipReady.Set();
            }
        }
    }

    private void Wake()
    {
        lock (_gate)
        {
            if (!_finished)
            {
                _wakePending = true;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
