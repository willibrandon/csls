namespace Csls.TestProcessHost;

/// <summary>
/// Provides unsigned wide enum storage for debugger invocation tests.
/// </summary>
internal enum DebuggerFixtureWideMode : ulong
{
    /// <summary>
    /// Selects the high-bit fixture mode.
    /// </summary>
    High = 0x8000_0000_0000_0000UL
}
