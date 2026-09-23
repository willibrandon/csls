namespace Csls.TestProcessHost;

/// <summary>
/// Provides flags enum storage for debugger value presentation tests.
/// </summary>
[Flags]
internal enum DebuggerFixtureOptions
{
    /// <summary>
    /// Selects no fixture options.
    /// </summary>
    None = 0,

    /// <summary>
    /// Selects the read fixture option.
    /// </summary>
    Read = 1,

    /// <summary>
    /// Selects the write fixture option.
    /// </summary>
    Write = 2,

    /// <summary>
    /// Selects the execute fixture option.
    /// </summary>
    Execute = 4,

    /// <summary>
    /// Selects the combined read and write fixture options.
    /// </summary>
    ReadWrite = Read | Write
}
