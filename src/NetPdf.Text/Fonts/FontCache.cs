// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts;

/// <summary>
/// Process-wide LRU cache mapping a font source identifier (file path or URI) to a
/// parsed <see cref="FontFace"/>. Multiple <c>PdfDocument</c> renders that touch the
/// same system font reuse the same parse instead of re-reading and re-parsing the file.
/// </summary>
/// <remarks>
/// <para>
/// Each cached face carries its own per-document subset state (the used-glyph bitmap
/// lives on <see cref="FontFace"/>); reusing one <see cref="FontFace"/> across multiple
/// documents would entangle their subsets. To keep that fence in place this cache stores
/// <i>byte arrays</i>, not faces — callers wrap the bytes in a fresh <see cref="FontFace"/>
/// per document via <see cref="FontFace.Load"/>. The expensive part is the byte read
/// from disk plus the OpenType parse; both are amortized once per source.
/// </para>
/// <para>
/// <b>Thread-safety.</b> Read / write paths are guarded by a single lock — fonts load
/// rarely (per-document, not per-page), so a coarse lock is the right trade-off versus
/// the complexity of a lock-free LRU.
/// </para>
/// </remarks>
internal sealed class FontCache
{
    private readonly object _lock = new();
    private readonly LinkedList<CacheNode> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheNode>> _index =
        new(StringComparer.Ordinal);

    /// <summary>Maximum number of distinct font sources to keep parsed in memory.</summary>
    public int Capacity { get; }

    public FontCache(int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    /// <summary>Number of cached entries currently resident.</summary>
    public int Count
    {
        get { lock (_lock) return _index.Count; }
    }

    /// <summary>
    /// Get the cached bytes for <paramref name="source"/>, loading via <paramref name="loader"/>
    /// on miss. The loader is invoked outside the cache lock so a slow disk read never
    /// blocks other callers; the result is published atomically via the lock.
    /// </summary>
    public ReadOnlyMemory<byte> GetOrAdd(string source, Func<string, ReadOnlyMemory<byte>> loader)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(loader);
        lock (_lock)
        {
            if (_index.TryGetValue(source, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Bytes;
            }
        }

        // Cache miss: load outside the lock.
        var bytes = loader(source);
        lock (_lock)
        {
            // Recheck after the load — a concurrent caller might have populated us.
            if (_index.TryGetValue(source, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Bytes;
            }
            var node = new LinkedListNode<CacheNode>(new CacheNode(source, bytes));
            _lru.AddFirst(node);
            _index[source] = node;
            EvictIfOverCapacity();
            return bytes;
        }
    }

    /// <summary>Remove a single entry from the cache; returns true if it was resident.</summary>
    public bool Remove(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_lock)
        {
            if (!_index.TryGetValue(source, out var node)) return false;
            _lru.Remove(node);
            _index.Remove(source);
            return true;
        }
    }

    /// <summary>Drop every cached entry.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lru.Clear();
            _index.Clear();
        }
    }

    private void EvictIfOverCapacity()
    {
        while (_index.Count > Capacity && _lru.Last is { } tail)
        {
            _index.Remove(tail.Value.Source);
            _lru.RemoveLast();
        }
    }

    private readonly record struct CacheNode(string Source, ReadOnlyMemory<byte> Bytes);
}
