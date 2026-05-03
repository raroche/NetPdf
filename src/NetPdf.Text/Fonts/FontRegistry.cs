// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts;

/// <summary>
/// Per-document registry of <see cref="FontFace"/> instances. Holds one face per
/// (family + weight + italic) tuple resolved for the document and wires the document's
/// embedding pipeline to a stable <c>BaseFont</c> name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per-document?</b> Subset state lives on <see cref="FontFace"/> (the used-glyph
/// bitmap). Two documents rendered in parallel must not share the same face instance, or
/// glyphs marked-used by one document would leak into the other's subset. The
/// process-wide <c>FontCache</c> handles the parse-once-and-share concern at the
/// byte-level layer below this registry.
/// </para>
/// <para>
/// <b>Lookup contract.</b> <see cref="TryGet"/> returns the previously-registered face
/// for a query, never resolves on miss; it is the caller's responsibility (typically a
/// document-level resolver) to register on miss. This keeps the registry policy-free —
/// it is a name → face map, nothing more.
/// </para>
/// </remarks>
internal sealed class FontRegistry : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<RegistryKey, FontFace> _byKey = [];
    private bool _disposed;

    /// <summary>Number of registered faces.</summary>
    public int Count
    {
        get { lock (_lock) return _byKey.Count; }
    }

    /// <summary>
    /// Try to retrieve the face previously registered for the family + weight + style +
    /// stretch tuple. Lookup is case-insensitive on family name (CSS rules treat
    /// font-family as case-insensitive ASCII). <paramref name="stretchCss"/> defaults to
    /// 5 (normal width) so existing callers that don't care about width keep working.
    /// </summary>
    public bool TryGet(string family, int weightCss, bool italic, out FontFace face, int stretchCss = 5)
    {
        ArgumentNullException.ThrowIfNull(family);
        var key = new RegistryKey(family, weightCss, stretchCss, italic);
        lock (_lock)
        {
            return _byKey.TryGetValue(key, out face!);
        }
    }

    /// <summary>
    /// Register <paramref name="face"/> under the family + weight + style + stretch tuple.
    /// If an entry already exists for that tuple, it is replaced; the previous face is
    /// <i>not</i> disposed (it may still be referenced by an in-flight rendering).
    /// <paramref name="stretchCss"/> defaults to 5 (normal width).
    /// </summary>
    public void Register(string family, int weightCss, bool italic, FontFace face, int stretchCss = 5)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(face);
        var key = new RegistryKey(family, weightCss, stretchCss, italic);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _byKey[key] = face;
        }
    }

    /// <summary>Snapshot of every registered face — for the embedding pipeline to walk at write time.</summary>
    public IReadOnlyList<FontFace> Faces
    {
        get
        {
            lock (_lock)
            {
                return [.. _byKey.Values];
            }
        }
    }

    public void Dispose()
    {
        FontFace[] toDispose;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = [.. _byKey.Values];
            _byKey.Clear();
        }
        foreach (var f in toDispose) f.Dispose();
    }

    private readonly record struct RegistryKey(string Family, int WeightCss, int StretchCss, bool Italic)
    {
        public bool Equals(RegistryKey other) =>
            string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase)
            && WeightCss == other.WeightCss
            && StretchCss == other.StretchCss
            && Italic == other.Italic;

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Family),
            WeightCss,
            StretchCss,
            Italic);
    }
}
