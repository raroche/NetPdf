// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace NetPdf.Paginate;

/// <summary>
/// Per Phase 3 plan — the document-scoped, layouter-side mutable state
/// passed by-ref through the layout call tree. Distinct from
/// <see cref="FragmentainerContext"/> (per-page state) — this carries
/// data that survives page boundaries: counter values, named-string
/// last-write tracking, the containing-block stack, the active writing
/// mode + bidi resolution baseline.
///
/// <para><b>ref struct.</b> The Phase 3 plan calls for
/// <c>LayoutContext ref struct</c> so the containing-block stack +
/// per-call mutation stay on the layouter's call stack with zero
/// heap pressure. Reference fields hosted in this struct are kept
/// minimal; mutable backing collections (the counter map + named
/// strings registry) are heap objects referenced through this
/// struct, which is fine — only the struct itself is non-allocatable.</para>
///
/// <para><b>What's NOT here.</b> Box-tree references, the cascade
/// result, the ComputedStyle pool — those live in the layouter's
/// regular parameters since they don't mutate during layout.</para>
/// </summary>
internal ref struct LayoutContext
{
    /// <summary>Available width (CSS px) for the box currently being
    /// laid out. Equal to its containing-block width minus padding +
    /// border on the inline axis. Updated on each containing-block
    /// push / pop.</summary>
    public double AvailableWidth;

    /// <summary>Available height (CSS px) — typically the
    /// fragmentainer's <see cref="FragmentainerContext.RemainingHeight"/>
    /// when the layouter is filling a page. <c>double.PositiveInfinity</c>
    /// when the height is intrinsic (e.g., a flex / grid container in
    /// a min/max-content sizing pass).</summary>
    public double AvailableHeight;

    /// <summary>Current writing mode. Mutates only at writing-mode
    /// boundaries (e.g., a child with <c>writing-mode: vertical-rl</c>
    /// inside an <c>horizontal-tb</c> parent); the layouter's caller
    /// snapshots + restores the value across the boundary.</summary>
    public WritingMode WritingMode;

    /// <summary>Per CSS Writing Modes L3 — Direction (LTR / RTL) at
    /// the containing-block level. Bidi resolution at the inline level
    /// happens in <c>NetPdf.Text</c>'s UAX #9 implementation; this
    /// flag tracks the BLOCK-level direction.</summary>
    public bool IsRtl;

    /// <summary>The active <see cref="FragmentainerContext"/>. Updated
    /// by the layouter when a page break commits + a new fragmentainer
    /// is allocated.</summary>
    public FragmentainerContext Fragmentainer;

    /// <summary>Document-scoped counter values per CSS Lists L3 §4.
    /// Keys are counter names (<c>page</c>, <c>pages</c>, author-defined);
    /// values are integer counter readings at the current layout
    /// position. <see cref="Counter(string, int)"/> updates +
    /// <see cref="ReadCounter(string)"/> reads.</summary>
    private Dictionary<string, int>? _counters;

    /// <summary>Construct a fresh root layout context. The caller
    /// (typically the Phase 3 layout entry point) pre-allocates the
    /// fragmentainer + page setup; this constructor only wires
    /// references.</summary>
    public LayoutContext(FragmentainerContext fragmentainer)
    {
        ArgumentNullException.ThrowIfNull(fragmentainer);
        AvailableWidth = fragmentainer.ContentAreaWidth;
        AvailableHeight = fragmentainer.ContentAreaHeight;
        WritingMode = WritingMode.HorizontalTb;
        IsRtl = false;
        Fragmentainer = fragmentainer;
        _counters = null;
    }

    /// <summary>Read the current value of <paramref name="counterName"/>;
    /// 0 when the counter has not been set in this layout pass.</summary>
    public int ReadCounter(string counterName)
    {
        ArgumentNullException.ThrowIfNull(counterName);
        if (_counters is null) return 0;
        return _counters.TryGetValue(counterName, out var v) ? v : 0;
    }

    /// <summary>Set <paramref name="counterName"/> to
    /// <paramref name="value"/>. Lazily allocates the backing
    /// dictionary so a layout pass that never uses author counters
    /// pays zero allocation overhead.</summary>
    public void Counter(string counterName, int value)
    {
        ArgumentNullException.ThrowIfNull(counterName);
        _counters ??= new Dictionary<string, int>(StringComparer.Ordinal);
        _counters[counterName] = value;
    }

    /// <summary>Increment <paramref name="counterName"/> by
    /// <paramref name="delta"/> (default 1) per CSS Lists L3
    /// <c>counter-increment</c>. Returns the new value.</summary>
    public int IncrementCounter(string counterName, int delta = 1)
    {
        var current = ReadCounter(counterName);
        var next = current + delta;
        Counter(counterName, next);
        return next;
    }
}
