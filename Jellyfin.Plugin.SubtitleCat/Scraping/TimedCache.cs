using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.SubtitleCat.Scraping;

/// <summary>
/// A small expiring cache.
/// </summary>
/// <typeparam name="TValue">The type held in the cache.</typeparam>
/// <remarks>
/// Hand-written rather than taking a dependency on a caching package, because keeping
/// the plugin to a single assembly is what lets the release zip stay one dll. The
/// access pattern is a handful of entries read a few times each within a minute, so
/// there is nothing here worth a library.
/// </remarks>
internal sealed class TimedCache<TValue>
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="TimedCache{TValue}"/> class.</summary>
    /// <param name="capacity">The number of entries to hold before evicting the oldest.</param>
    public TimedCache(int capacity = 256)
    {
        _capacity = capacity < 1 ? 1 : capacity;
    }

    /// <summary>Gets a live entry, or false when it is missing or expired.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The cached value, when one is live.</param>
    public bool TryGet(string key, out TValue? value)
    {
        value = default;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.ExpiresUtc <= DateTime.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        value = entry.Value;
        return true;
    }

    /// <summary>Stores a value, replacing any existing entry for the key.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="lifetime">How long the value stays live. A non-positive span stores nothing.</param>
    public void Set(string key, TValue value, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            return;
        }

        _entries[key] = new Entry(value, DateTime.UtcNow.Add(lifetime));

        if (_entries.Count > _capacity)
        {
            Trim();
        }
    }

    /// <summary>Empties the cache. Used when the configuration changes.</summary>
    public void Clear() => _entries.Clear();

    private void Trim()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _entries.Where(p => p.Value.ExpiresUtc <= now))
        {
            _entries.TryRemove(pair.Key, out _);
        }

        // Still over capacity means everything is live, so drop whatever expires soonest.
        var excess = _entries.Count - _capacity;
        if (excess <= 0)
        {
            return;
        }

        foreach (var pair in _entries.OrderBy(p => p.Value.ExpiresUtc).Take(excess))
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    private sealed record Entry(TValue Value, DateTime ExpiresUtc);
}
