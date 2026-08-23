using Csls.Protocol;
using System.Collections.Concurrent;

namespace Csls.Workspaces;

/// <summary>
/// Holds bounded hover results for one immutable Roslyn project.
/// </summary>
internal sealed class RazorHoverProjectCache
{
    private const int MaximumHoverResults = 256;
    private readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<Position, Hover>> _hovers;
    private int _hoverCount;

    /// <summary>
    /// Initializes path-keyed caches using platform file-system comparison rules.
    /// </summary>
    internal RazorHoverProjectCache()
    {
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _hovers = new ConcurrentDictionary<
            string,
            ConcurrentDictionary<Position, Hover>>(pathComparer);
    }

    /// <summary>
    /// Gets a successful hover previously resolved for the same immutable snapshot.
    /// </summary>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="hover">The cached hover when present.</param>
    /// <returns>True when the exact path and position have a cached result.</returns>
    internal bool TryGetHover(string path, Position position, out Hover? hover)
    {
        if (_hovers.TryGetValue(
            path,
            out ConcurrentDictionary<Position, Hover>? positions) &&
            positions.TryGetValue(position, out Hover? cachedHover))
        {
            hover = cachedHover;
            return true;
        }

        hover = null;
        return false;
    }

    /// <summary>
    /// Adds one successful hover while keeping project snapshot memory bounded.
    /// </summary>
    /// <param name="path">The absolute Razor document path.</param>
    /// <param name="position">The zero-based UTF-16 Razor position.</param>
    /// <param name="hover">The immutable hover response.</param>
    internal void TryAddHover(string path, Position position, Hover hover)
    {
        ArgumentNullException.ThrowIfNull(hover);
        if (Volatile.Read(ref _hoverCount) >= MaximumHoverResults)
        {
            return;
        }

        ConcurrentDictionary<Position, Hover> positions = _hovers.GetOrAdd(
            path,
            static _ => []);
        if (positions.TryAdd(position, hover) &&
            Interlocked.Increment(ref _hoverCount) > MaximumHoverResults)
        {
            positions.TryRemove(position, out _);
            Interlocked.Decrement(ref _hoverCount);
        }
    }
}
