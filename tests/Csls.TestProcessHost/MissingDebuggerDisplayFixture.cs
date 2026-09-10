using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes a debugger display expression whose field does not exist.
/// </summary>
[DebuggerDisplay("missing={_missing}")]
internal sealed class MissingDebuggerDisplayFixture;
