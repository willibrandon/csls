using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes multiple debugger displays to prove deterministic first-attribute selection.
/// </summary>
[DebuggerDisplay("first")]
[DebuggerDisplay("second")]
internal sealed class MultipleDebuggerDisplayFixture;
