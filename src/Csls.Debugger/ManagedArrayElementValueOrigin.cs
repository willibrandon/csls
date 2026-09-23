namespace Csls.Debugger;

/// <summary>
/// Identifies a physical row-major element position within its original array.
/// </summary>
/// <param name="Parent">The original array's physical origin.</param>
/// <param name="Index">The zero-based linear position, after applying each dimension's lower bound.</param>
internal sealed record ManagedArrayElementValueOrigin(
    ManagedValueOrigin Parent,
    int Index) : ManagedValueOrigin;
