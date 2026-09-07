namespace Csls.Debugger.Tests;

/// <summary>
/// Preserves a failed native dump writer's target identity and captured diagnostic output.
/// </summary>
internal sealed class DebuggerDumpCaptureException : IOException
{
    /// <summary>
    /// Creates a dump capture failure without native diagnostics.
    /// </summary>
    public DebuggerDumpCaptureException()
    {
    }

    /// <summary>
    /// Creates a dump capture failure with an explanatory message.
    /// </summary>
    /// <param name="message">The failure description.</param>
    public DebuggerDumpCaptureException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a dump capture failure retaining its underlying cause.
    /// </summary>
    /// <param name="message">The failure description.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DebuggerDumpCaptureException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Records the native failure after the target has exited and its streams have drained.
    /// </summary>
    /// <param name="processId">The terminated test-owned target.</param>
    /// <param name="dumpPath">The attempted dump destination.</param>
    /// <param name="standardOutput">The bounded target standard-output tail.</param>
    /// <param name="standardError">The bounded target standard-error tail.</param>
    /// <param name="innerException">The original diagnostics protocol failure.</param>
    internal DebuggerDumpCaptureException(int processId, string dumpPath, string standardOutput,
        string standardError, Exception innerException)
        : base($"Dump capture for process {processId} at '{dumpPath}' failed.{Environment.NewLine}" +
            $"stdout: {standardOutput}{Environment.NewLine}stderr: {standardError}", innerException)
    {
        ProcessId = processId;
        DumpPath = dumpPath;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>
    /// Gets the terminated target's operating-system identifier.
    /// </summary>
    internal int ProcessId { get; }

    /// <summary>
    /// Gets the attempted destination inside the released fixture directory.
    /// </summary>
    internal string DumpPath { get; } = string.Empty;

    /// <summary>
    /// Gets the captured standard-output tail.
    /// </summary>
    internal string StandardOutput { get; } = string.Empty;

    /// <summary>
    /// Gets the captured standard-error tail.
    /// </summary>
    internal string StandardError { get; } = string.Empty;
}
