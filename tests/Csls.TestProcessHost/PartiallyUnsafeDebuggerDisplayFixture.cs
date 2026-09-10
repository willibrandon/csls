using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Provides a safe value template with unsafe optional name and type templates.
/// </summary>
[DebuggerDisplay("safe={_value}", Name = "{Computed}", Type = "{Computed}")]
internal sealed class PartiallyUnsafeDebuggerDisplayFixture
{
    /// <summary>
    /// Stores the value rendered by the safe display template.
    /// </summary>
    internal readonly int _value = 66;

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
