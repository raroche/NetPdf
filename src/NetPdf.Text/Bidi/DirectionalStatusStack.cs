// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// The BD13 directional status stack (UAX #9 §3.3.2). Each entry tracks an embedding
/// <see cref="Entry.Level"/>, an active <see cref="Entry.Override"/>, and whether that
/// entry corresponds to an isolate (<see cref="Entry.IsIsolate"/>). The X-rules push
/// entries on encountering LRE/RLE/LRO/RLO/LRI/RLI/FSI and pop on PDF/PDI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Maximum depth.</b> UAX #9 BD2 caps embedding levels at <see cref="MaxEmbeddingLevel"/>
/// (125) — push attempts beyond that increment the appropriate overflow counter instead
/// of growing the stack. The fixed-size backing array is sized to that cap plus the
/// implicit base entry (paragraph level) for total <c>MaxEmbeddingLevel + 1</c>; the stack
/// can never legitimately grow larger and overflow attempts are tracked by the X-rule
/// runner via the overflow counters, not by stack growth.
/// </para>
/// <para>
/// <b>Allocation.</b> Backed by a stack-friendly array; no dynamic resizing. The X-rule
/// runner allocates one stack per paragraph and reuses it for the duration of that pass.
/// </para>
/// </remarks>
internal sealed class DirectionalStatusStack
{
    /// <summary>Maximum embedding level per UAX #9 BD2.</summary>
    public const byte MaxEmbeddingLevel = 125;

    /// <summary>One entry on the directional status stack.</summary>
    public readonly record struct Entry(byte Level, DirectionalOverride Override, bool IsIsolate);

    /// <summary>BD2 cap + 1 implicit base — every legitimate stack stays under this.</summary>
    private readonly Entry[] _items = new Entry[MaxEmbeddingLevel + 2];
    private int _depth;

    /// <summary>The number of entries currently on the stack (≥ 1 once initialized).</summary>
    public int Depth => _depth;

    /// <summary>The top (most-recent) entry. Throws if the stack is empty.</summary>
    public Entry Top
    {
        get
        {
            if (_depth == 0)
            {
                throw new InvalidOperationException("Directional status stack is empty.");
            }
            return _items[_depth - 1];
        }
    }

    /// <summary>Push a new entry. Caller is responsible for honoring the BD2 cap.</summary>
    public void Push(byte level, DirectionalOverride @override, bool isIsolate)
    {
        if (_depth >= _items.Length)
        {
            // Should never happen — the X-rule runner gates pushes on the BD2 cap. Throw
            // to surface programming errors immediately rather than silently truncating.
            throw new InvalidOperationException(
                $"Directional status stack overflow: depth {_depth} exceeds BD2 cap.");
        }
        _items[_depth++] = new Entry(level, @override, isIsolate);
    }

    /// <summary>Pop the top entry. Throws if the stack is empty.</summary>
    public void Pop()
    {
        if (_depth == 0)
        {
            throw new InvalidOperationException("Directional status stack is empty.");
        }
        _depth--;
    }

    /// <summary>Reset the stack to empty so the same instance can be reused for another paragraph.</summary>
    public void Clear() => _depth = 0;
}
