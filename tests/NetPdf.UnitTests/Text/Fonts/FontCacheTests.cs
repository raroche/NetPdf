// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts;

/// <summary>
/// Pure-logic tests for the process-wide LRU <see cref="FontCache"/>. No font bytes
/// required — the loader callback returns synthetic byte arrays so the LRU and
/// concurrency contracts can be exercised without disk I/O.
/// </summary>
public sealed class FontCacheTests
{
    [Fact]
    public void Constructor_throws_for_zero_or_negative_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FontCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FontCache(-1));
    }

    [Fact]
    public void GetOrAdd_loads_on_miss_and_caches_subsequent_lookups()
    {
        var cache = new FontCache(capacity: 4);
        var loadCount = 0;
        var bytes = cache.GetOrAdd("/fake/a.ttf", _ => { loadCount++; return new byte[] { 1, 2, 3 }; });
        Assert.Equal(3, bytes.Length);
        Assert.Equal(1, loadCount);

        // Second call hits the cache; loader is not invoked again.
        var again = cache.GetOrAdd("/fake/a.ttf", _ => { loadCount++; return new byte[] { 9, 9, 9 }; });
        Assert.Equal(3, again.Length);
        Assert.Equal(1, loadCount);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Capacity_eviction_drops_least_recently_used_entry()
    {
        var cache = new FontCache(capacity: 3);
        cache.GetOrAdd("/a", _ => new byte[] { 1 });
        cache.GetOrAdd("/b", _ => new byte[] { 2 });
        cache.GetOrAdd("/c", _ => new byte[] { 3 });
        // Touch /a + /c so /b becomes the LRU entry.
        cache.GetOrAdd("/a", _ => throw new InvalidOperationException("should not reload"));
        cache.GetOrAdd("/c", _ => throw new InvalidOperationException("should not reload"));
        // Insert /d — must evict /b (least recently used) and keep /a, /c, /d.
        cache.GetOrAdd("/d", _ => new byte[] { 4 });
        Assert.Equal(3, cache.Count);

        // /a, /c, /d should remain cached (loader must NOT run); /b must reload.
        var bReloads = 0;
        cache.GetOrAdd("/a", _ => throw new InvalidOperationException("should not reload"));
        cache.GetOrAdd("/c", _ => throw new InvalidOperationException("should not reload"));
        cache.GetOrAdd("/d", _ => throw new InvalidOperationException("should not reload"));
        cache.GetOrAdd("/b", _ => { bReloads++; return new byte[] { 22 }; });
        Assert.Equal(1, bReloads);
    }

    [Fact]
    public void Remove_drops_a_specific_entry()
    {
        var cache = new FontCache(capacity: 4);
        cache.GetOrAdd("/x", _ => new byte[] { 1 });
        Assert.True(cache.Remove("/x"));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.Remove("/x"));
    }

    [Fact]
    public void Clear_drops_every_entry()
    {
        var cache = new FontCache(capacity: 4);
        cache.GetOrAdd("/a", _ => new byte[] { 1 });
        cache.GetOrAdd("/b", _ => new byte[] { 2 });
        cache.GetOrAdd("/c", _ => new byte[] { 3 });
        Assert.Equal(3, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void GetOrAdd_throws_on_null_args()
    {
        var cache = new FontCache();
        Assert.Throws<ArgumentNullException>(() => cache.GetOrAdd(null!, _ => default));
        Assert.Throws<ArgumentNullException>(() => cache.GetOrAdd("/a", null!));
    }

    [Fact]
    public void Default_capacity_is_reasonable()
    {
        var cache = new FontCache();
        Assert.True(cache.Capacity >= 16, "default capacity should accommodate a typical document's font set");
    }
}
