using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes malformed debugger presentation metadata for safe fallback coverage.
/// </summary>
[DebuggerDisplay("broken {")]
internal sealed class MalformedDebuggerDisplayFixture;
