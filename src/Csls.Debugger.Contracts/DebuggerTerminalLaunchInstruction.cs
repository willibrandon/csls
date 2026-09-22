namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries an exact target invocation from the debugger worker to its terminal launcher.
/// </summary>
public sealed class DebuggerTerminalLaunchInstruction
{
    /// <summary>
    /// Gets the absolute target assembly or executable path.
    /// </summary>
    public required string Program { get; init; }

    /// <summary>
    /// Gets the absolute working directory for the target.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the exact target argument sequence.
    /// </summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Gets the complete target environment, separate from launcher coordination settings.
    /// </summary>
    public required Dictionary<string, string> Environment { get; init; }

    /// <summary>
    /// Gets the optional host used for a managed assembly.
    /// </summary>
    public string? RuntimeHostPath { get; init; }

    /// <summary>
    /// Gets whether CoreCLR pauses at its diagnostic startup gate.
    /// </summary>
    public bool SuspendForDebugging { get; init; }
}
