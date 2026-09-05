namespace Csls.Debugger;

/// <summary>
/// Shares retirement state across one materialized enumerable and its retained descendants.
/// </summary>
internal sealed class ManagedResultsViewLifetime
{
    /// <summary>
    /// Gets whether target execution or a direct assignment retired this snapshot.
    /// </summary>
    internal bool IsRetired { get; private set; }

    /// <summary>
    /// Prevents further inspection through this snapshot's value and memory references.
    /// </summary>
    internal void Retire() => IsRetired = true;
}
