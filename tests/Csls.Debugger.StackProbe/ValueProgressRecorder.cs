using Csls.Debugger.Contracts;

namespace Csls.Debugger.StackProbe;

/// <summary>
/// Records real value inspection and performs client cancellation at an observed checkpoint.
/// </summary>
/// <param name="cancellation">The requesting client's cancellation source.</param>
/// <param name="checkpoint">The formatted-value count at which cancellation is requested.</param>
/// <param name="mode">The client notification behavior to exercise.</param>
internal sealed class ValueProgressRecorder(CancellationTokenSource cancellation, int checkpoint, string mode)
    : IProgress<DebugValueReadProgress>
{
    /// <summary>
    /// Gets the progress delivered synchronously by the real engine actor.
    /// </summary>
    internal List<DebugValueReadProgress> Updates { get; } = [];

    /// <inheritdoc />
    public void Report(DebugValueReadProgress value)
    {
        Updates.Add(value);
        if ((checkpoint > 0 && value.State == DebugValueReadState.Reading && value.FormattedValues >= checkpoint) ||
            (mode == "cancel-completed" && value.State == DebugValueReadState.Completed))
        {
            cancellation.Cancel();
        }

        bool failNotification = mode switch
        {
            "fail-reading" => value.State == DebugValueReadState.Reading,
            "fail-completed" => value.State == DebugValueReadState.Completed,
            "fail-failed" => value.State == DebugValueReadState.Failed,
            "fail-canceled" => value.State == DebugValueReadState.Canceled,
            _ => false
        };
        if (failNotification)
        {
            throw new IOException("The requesting value progress client failed.");
        }

        if (mode == "unexpected" && value.State == DebugValueReadState.Reading)
        {
            throw new FormatException("The requesting value progress client rejected its update.");
        }
    }
}
