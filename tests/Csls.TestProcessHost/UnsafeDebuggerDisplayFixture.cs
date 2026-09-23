using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Exposes a display property that automatic presentation must never execute.
/// </summary>
[DebuggerDisplay("{Computed}", Name = "{Computed}", Type = "{Computed}")]
internal sealed class UnsafeDebuggerDisplayFixture : InheritedDebuggerDisplayBaseFixture
{
    private int _accessCount;

    private string Computed
    {
        get
        {
            _accessCount++;
            return "executed";
        }
    }
}
