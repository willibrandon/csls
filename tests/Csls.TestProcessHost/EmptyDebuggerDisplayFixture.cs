using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes explicitly empty debugger-display components for metadata parity coverage.
/// </summary>
[DebuggerDisplay("", Name = "", Type = "")]
internal sealed class EmptyDebuggerDisplayFixture;
