// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Immutable;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// Per Phase 3 Task 17 cycle 0a (+ post-PR-#89 review hardening) —
/// CSS Grid L1 typed value model. Covers the AST shapes for
/// <c>grid-template-rows</c> / <c>grid-template-columns</c>
/// (= <see cref="TrackList"/>) and <c>grid-row-start</c> / <c>-end</c> /
/// <c>grid-column-start</c> / <c>-end</c> (= <see cref="GridLineValue"/>).
///
/// <para><b>Design constraint per PR-#88 review P2 #4:</b> the
/// track-AST uses a flat <see cref="TrackEntry"/> struct (= no
/// inner struct recursion).</para>
///
/// <para><b>Design constraint per PR-#88 review P2 #6 +
/// PR-#89 review P1 #2:</b> the AST is stored as parsed
/// (= preserving named lines, <c>repeat()</c> groups, and
/// <c>auto-fill</c> / <c>auto-fit</c> markers). Layout-time
/// expansion in <c>GridLayouter</c> resolves <c>auto-fill</c> /
/// <c>auto-fit</c> counts against the container size.
/// <c>repeat(&lt;integer&gt;, ...)</c> expansion happens at layout
/// time too (= the AST keeps the compact form; layout-time
/// expansion applies the
/// <see cref="TrackList.MaxExpandedTrackCount"/> DoS guard).</para>
///
/// <para><b>Storage decision per PR-#89 review P1 #3:</b> all
/// <see cref="TrackList"/> + <see cref="GridLineValue"/> values
/// flow through the ComputedSlot side-table pattern (= uniform
/// storage; ComputedSlot carries the side-table index). Cycle 0b
/// wires the dispatcher; cycle 0a establishes the AST contract.</para>
///
/// <para><b>Validation contract per PR-#89 review P3 #8:</b> AST
/// constructors validate invariants (= reject NaN/±Inf/negative
/// sizes, zero line numbers per CSS Grid §8.3, non-positive spans,
/// invalid repeat counts, empty named lines). The parser in cycle
/// 0b is the only intended source of these types; tests below pin
/// the invariants.</para>
/// </summary>

/// <summary>Per CSS Grid L1 §7.2 — the kind of one entry in a track
/// list. Determines which payload fields of <see cref="TrackEntry"/>
/// are read.
///
/// <para><b>Post-PR-#89 review P2 #5:</b>
/// <see cref="Auto"/> / <see cref="MinContent"/> /
/// <see cref="MaxContent"/> are three DISTINCT kinds even though
/// cycle 3's L19-approximation initially treats them identically.
/// The AST preserves the authored keyword so future intrinsic-sizing
/// work doesn't need to retro-parse.</para></summary>
internal enum GridTrackKind : byte
{
    /// <summary><c>&lt;length-percentage&gt;</c> track. Value in
    /// <see cref="TrackEntry.LengthPx"/> when
    /// <see cref="TrackEntry.IsPercentage"/> is false; in
    /// <see cref="TrackEntry.LengthPx"/> interpreted as percent
    /// (= 0-100, possibly more) when true. Per PR-#89 review P2 #6
    /// the field-pair carries BOTH px + percent so the AST doesn't
    /// lose the authored unit.</summary>
    Length = 0,

    /// <summary><c>&lt;flex&gt;</c> track (e.g., <c>1fr</c>) — value
    /// in <see cref="TrackEntry.FrValue"/>. Cycle 2 ships the §11.7
    /// "Find the Size of an fr" algorithm.</summary>
    Fr = 1,

    /// <summary><c>auto</c> — the keyword default for intrinsic
    /// sizing. Per PR-#89 review P2 #5 distinct from
    /// <see cref="MinContent"/> / <see cref="MaxContent"/> even
    /// though the cycle-3 layouter approximation may treat them
    /// identically.</summary>
    Auto = 2,

    /// <summary><c>min-content</c> — track sized to the largest of
    /// each item's min-content contribution per CSS Grid §11.5.</summary>
    MinContent = 3,

    /// <summary><c>max-content</c> — track sized to the largest of
    /// each item's max-content contribution per CSS Grid §11.5.</summary>
    MaxContent = 4,

    /// <summary><c>minmax(min, max)</c> — sub-args in
    /// <see cref="TrackEntry.MinSubKind"/> + <see cref="TrackEntry.MaxSubKind"/>
    /// + the corresponding LengthPx / FrValue fields. Per CSS Grid §7.2.4,
    /// the <c>min</c> arg can be <c>&lt;length-percentage&gt;</c> /
    /// <c>auto</c> / <c>min-content</c> / <c>max-content</c>
    /// (= Fr is INVALID in min); the <c>max</c> arg can additionally
    /// be Fr.</summary>
    MinMax = 5,

    /// <summary><c>fit-content(limit)</c> — limit in
    /// <see cref="TrackEntry.LengthPx"/> /
    /// <see cref="TrackEntry.IsPercentage"/>. Per CSS Grid §7.2.2 the
    /// effective size formula is
    /// <c>max(auto-minimum, min(limit, max-content))</c>.</summary>
    FitContent = 6,
}

/// <summary>One entry in a parsed track list. The struct is flat
/// (= zero recursion) per PR-#88 review P2 #4. The
/// <see cref="Kind"/> tag selects which payload fields are read;
/// unused fields are zero.
///
/// <para><b>Length representation per PR-#89 review P2 #6:</b>
/// <see cref="LengthPx"/> carries either pixels OR percent value
/// (= 0-100), gated by <see cref="IsPercentage"/>. Calc() is L19+
/// scope; the cycle-0b parser rejects calc() track sizes.</para>
///
/// <para><b>Construction:</b> use the
/// <see cref="ForLength"/> / <see cref="ForPercentage"/> /
/// <see cref="ForFr"/> / <see cref="ForAuto"/> /
/// <see cref="ForMinContent"/> / <see cref="ForMaxContent"/> /
/// <see cref="ForMinMax"/> / <see cref="ForFitContent"/> factory
/// methods. The factories validate invariants (= rejects
/// NaN/±Inf/negative) per PR-#89 review P3 #8.</para></summary>
internal readonly record struct TrackEntry
{
    public GridTrackKind Kind { get; init; }
    /// <summary>For Length / FitContent: the length-or-percent value.
    /// Interpretation gated by <see cref="IsPercentage"/>.</summary>
    public double LengthPx { get; init; }
    /// <summary>For Length / FitContent: true if <see cref="LengthPx"/>
    /// is a percentage (= 0-100) rather than pixels. Layout-time
    /// resolution against the container size produces the px value.</summary>
    public bool IsPercentage { get; init; }
    /// <summary>For Fr: the flex factor (positive finite).</summary>
    public double FrValue { get; init; }
    /// <summary>For MinMax: the min arg's kind. Per §7.2.4 must be
    /// one of Length / Auto / MinContent / MaxContent (= Fr is
    /// invalid in min).</summary>
    public GridTrackKind MinSubKind { get; init; }
    public double MinSubLengthPx { get; init; }
    public bool MinSubIsPercentage { get; init; }
    /// <summary>For MinMax: the max arg's kind. Can be any of
    /// Length / Auto / MinContent / MaxContent / Fr.</summary>
    public GridTrackKind MaxSubKind { get; init; }
    public double MaxSubLengthPx { get; init; }
    public bool MaxSubIsPercentage { get; init; }
    public double MaxSubFrValue { get; init; }

    // =====================================================================
    //  Validating factory methods (per PR-#89 review P3 #8).
    //  Constructors are public-init so callers CAN bypass the validators
    //  in pathological tests; production code should use these factories.
    // =====================================================================

    /// <summary>A pixel-length track. Validates finite + non-negative.</summary>
    public static TrackEntry ForLength(double px)
    {
        if (!double.IsFinite(px) || px < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(px),
                $"TrackEntry length must be finite + non-negative; got {px}");
        }
        return new TrackEntry { Kind = GridTrackKind.Length, LengthPx = px };
    }

    /// <summary>A percentage-length track. Validates finite + non-negative.</summary>
    public static TrackEntry ForPercentage(double percent)
    {
        if (!double.IsFinite(percent) || percent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(percent),
                $"TrackEntry percentage must be finite + non-negative; got {percent}");
        }
        return new TrackEntry
        {
            Kind = GridTrackKind.Length,
            LengthPx = percent,
            IsPercentage = true,
        };
    }

    /// <summary>An fr track. Validates finite + non-negative
    /// (= 0fr is valid per §7.2.3 — receives 0 of leftover space).</summary>
    public static TrackEntry ForFr(double fr)
    {
        if (!double.IsFinite(fr) || fr < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fr),
                $"TrackEntry fr factor must be finite + non-negative; got {fr}");
        }
        return new TrackEntry { Kind = GridTrackKind.Fr, FrValue = fr };
    }

    public static TrackEntry ForAuto() => new() { Kind = GridTrackKind.Auto };
    public static TrackEntry ForMinContent() => new() { Kind = GridTrackKind.MinContent };
    public static TrackEntry ForMaxContent() => new() { Kind = GridTrackKind.MaxContent };

    /// <summary>A minmax(min, max) track. Validates the per-arg
    /// kind constraints from §7.2.4 (= Fr forbidden in min).</summary>
    public static TrackEntry ForMinMax(TrackEntry min, TrackEntry max)
    {
        if (min.Kind == GridTrackKind.Fr)
        {
            throw new ArgumentException(
                "minmax() min arg cannot be <flex> per CSS Grid L1 §7.2.4",
                nameof(min));
        }
        if (min.Kind == GridTrackKind.MinMax || min.Kind == GridTrackKind.FitContent ||
            max.Kind == GridTrackKind.MinMax || max.Kind == GridTrackKind.FitContent)
        {
            throw new ArgumentException(
                "minmax() args cannot themselves be minmax() or fit-content() per §7.2.4");
        }
        return new TrackEntry
        {
            Kind = GridTrackKind.MinMax,
            MinSubKind = min.Kind,
            MinSubLengthPx = min.LengthPx,
            MinSubIsPercentage = min.IsPercentage,
            MaxSubKind = max.Kind,
            MaxSubLengthPx = max.LengthPx,
            MaxSubIsPercentage = max.IsPercentage,
            MaxSubFrValue = max.FrValue,
        };
    }

    /// <summary>A fit-content(limit) track. Validates finite +
    /// non-negative.</summary>
    public static TrackEntry ForFitContent(double limitPx, bool isPercentage = false)
    {
        if (!double.IsFinite(limitPx) || limitPx < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitPx),
                $"fit-content() limit must be finite + non-negative; got {limitPx}");
        }
        return new TrackEntry
        {
            Kind = GridTrackKind.FitContent,
            LengthPx = limitPx,
            IsPercentage = isPercentage,
        };
    }
}

/// <summary>Items that can appear INSIDE a <c>repeat(N, ...)</c>
/// pattern. Per PR-#89 review P1 #2: CSS Grid L1 §7.2.3 allows
/// <c>repeat(N, [name] &lt;track&gt; [name])</c> — named lines
/// can interleave with track entries inside the repeat group, and
/// the names repeat with each repetition. Excludes nested repeat()
/// (= forbidden by §7.2.3).</summary>
internal abstract record TrackRepeatItem;

/// <summary>An inline track entry inside a repeat() group.</summary>
internal sealed record TrackRepeatEntry(TrackEntry Entry) : TrackRepeatItem;

/// <summary>A named line inside a repeat() group. Repeats N times
/// with the pattern; cycle 7's name resolver concatenates
/// occurrences from each repetition.</summary>
internal sealed record TrackRepeatNamedLine(string Name) : TrackRepeatItem
{
    /// <summary>Validated constructor — empty names are not valid
    /// per CSS custom-ident grammar.</summary>
    public static TrackRepeatNamedLine Create(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                "Named line identifier cannot be null or empty per CSS custom-ident grammar",
                nameof(name));
        }
        return new TrackRepeatNamedLine(name);
    }
}

/// <summary>One <c>repeat()</c> group in a parsed track list. May
/// expand to multiple items at layout time.
///
/// <para><see cref="Count"/> encoding (= positive: explicit count;
/// 0: <c>auto-fill</c>; -1: <c>auto-fit</c>).</para>
///
/// <para><b>DoS guard per PR-#89 review P2 #7:</b>
/// <see cref="MaxRepeatCount"/> bounds the parser-accepted count;
/// hostile CSS like <c>repeat(1000000000, 1px)</c> rejects at
/// parse time. Layout-time expansion also caps total expanded
/// track count per <see cref="TrackList.MaxExpandedTrackCount"/>
/// to prevent CPU/memory DoS via multiple large repeats.</para>
///
/// <para>Per PR-#89 review P1 #2 the <see cref="Pattern"/> array
/// uses <see cref="TrackRepeatItem"/>, so named lines INSIDE the
/// repeat group preserve their position + repeat with each
/// iteration.</para></summary>
internal sealed record TrackRepeat(
    int Count,
    ImmutableArray<TrackRepeatItem> Pattern)
{
    /// <summary>Per PR-#89 review P2 #7 — upper bound on the
    /// repeat() count the parser accepts. <c>repeat(1000000000, 1px)</c>
    /// is rejected. Combined with
    /// <see cref="TrackList.MaxExpandedTrackCount"/>, this caps
    /// total CPU + allocation per grid declaration.</summary>
    public const int MaxRepeatCount = 10_000;

    /// <summary>Validating factory. Per PR-#89 review P3 #8 +
    /// P2 #7: count must be in [-1, MaxRepeatCount]; pattern must
    /// be non-empty.</summary>
    public static TrackRepeat Create(int count, ImmutableArray<TrackRepeatItem> pattern)
    {
        if (count < -1 || count > MaxRepeatCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count),
                $"TrackRepeat count must be in [-1, {MaxRepeatCount}] "
                + $"(-1 = auto-fit, 0 = auto-fill, positive = explicit count); "
                + $"got {count}.");
        }
        if (pattern.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "TrackRepeat pattern cannot be null or empty", nameof(pattern));
        }
        return new TrackRepeat(count, pattern);
    }
}

/// <summary>The parsed AST for a <c>grid-template-rows</c> /
/// <c>-columns</c> declaration. Stored on <c>ComputedStyle</c> via
/// the side-table pattern (= the ComputedSlot for the property
/// carries a <c>SideTableIndex</c> tag pointing to the
/// <see cref="TrackList"/> in a typed dictionary).
///
/// <para><b>Items list shape</b>: a sequence of
/// <see cref="TrackListItem"/> abstract entries. The CSS source
/// order is preserved so layout-time expansion produces the
/// spec-correct sequence (= named lines interleave with track
/// entries + repeat groups). The empty list represents
/// <c>grid-template-rows: none</c> (= the property default).</para></summary>
internal sealed record TrackList(
    ImmutableArray<TrackListItem> Items)
{
    /// <summary>Per PR-#89 review P2 #7 — total expanded track-count
    /// cap. Layout-time <c>repeat()</c> expansion + multiple track
    /// entries are summed against this limit; exceeding values raise
    /// a diagnostic + truncate. Combined with
    /// <see cref="TrackRepeat.MaxRepeatCount"/> this bounds the total
    /// allocation per grid container.</summary>
    public const int MaxExpandedTrackCount = 50_000;

    /// <summary>The CSS property-default value (= <c>none</c>) per
    /// CSS Grid L1 §7.2. No tracks → grid is implicit-only (all
    /// rows/columns auto-added per <c>grid-auto-rows</c> /
    /// <c>grid-auto-columns</c>).</summary>
    public static readonly TrackList None =
        new(ImmutableArray<TrackListItem>.Empty);
}

/// <summary>Discriminated union of items in a <see cref="TrackList"/>.
/// Sealed hierarchy so callers can exhaustively switch.</summary>
internal abstract record TrackListItem;

/// <summary>An inline track entry (= <c>&lt;length&gt;</c>, <c>fr</c>,
/// <c>auto</c>, <c>minmax(...)</c>, <c>fit-content(...)</c>).</summary>
internal sealed record TrackListEntry(TrackEntry Entry) : TrackListItem;

/// <summary>A <c>repeat()</c> group.</summary>
internal sealed record TrackListRepeat(TrackRepeat Repeat) : TrackListItem;

/// <summary>A named line at TOP LEVEL of the track list (= NOT
/// inside a repeat). Cycle 7 reads these for
/// <c>grid-row-start: &lt;name&gt;</c> resolution.</summary>
internal sealed record TrackListNamedLine(string Name) : TrackListItem
{
    /// <summary>Validated factory — rejects null/empty names.</summary>
    public static TrackListNamedLine Create(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                "Named line identifier cannot be null or empty per CSS custom-ident grammar",
                nameof(name));
        }
        return new TrackListNamedLine(name);
    }
}

/// <summary>Per CSS Grid L1 §8.3 — the kind of a grid-line value.</summary>
internal enum GridLineKind : byte
{
    /// <summary><c>auto</c> — the default per §8.3.</summary>
    Auto = 0,

    /// <summary><c>&lt;integer&gt;</c> line number, optionally with a
    /// named-line qualifier (= e.g., <c>2</c> or <c>foo 2</c>).
    /// <see cref="GridLineValue.LineNumber"/> stores the number;
    /// <see cref="GridLineValue.NamedLine"/> stores the optional name.
    /// Negative numbers count from the end (= -1 is last line).
    /// Zero is invalid per §8.3.</summary>
    LineNumber = 1,

    /// <summary><c>span</c> with an integer count + optional named
    /// line (= e.g., <c>span 2</c>, <c>span foo</c>, <c>span foo 2</c>,
    /// <c>span 2 foo</c>). <see cref="GridLineValue.LineNumber"/> stores
    /// the span count (must be ≥ 1 when present); zero indicates
    /// "no count provided — span to next occurrence of name".
    /// <see cref="GridLineValue.NamedLine"/> stores the optional name.</summary>
    Span = 2,

    /// <summary><c>&lt;custom-ident&gt;</c> only — a named line with
    /// no integer occurrence. Cycle 7 resolves this against the
    /// container's named lines. <see cref="GridLineValue.NamedLine"/>
    /// stores the name; <see cref="GridLineValue.LineNumber"/> is 0.</summary>
    NamedLine = 3,
}

/// <summary>A grid-line value (= the value of one of the four
/// grid-row-start / -end / grid-column-start / -end longhands).
/// Per CSS Grid L1 §8.3, the grammar is:
/// <c>auto | &lt;custom-ident&gt; | [ &lt;integer&gt; &amp;&amp;
/// &lt;custom-ident&gt;? ] | [ span &amp;&amp; [ &lt;integer&gt; ||
/// &lt;custom-ident&gt; ] ]</c>.
///
/// <para><b>Storage decision per PR-#89 review P1 #3:</b> all
/// non-default values flow through the ComputedSlot side-table
/// pattern (= uniform; the cycle-0a XML claim that LineNumber +
/// Span fit inline was incorrect since the optional named-line
/// qualifier always carries a string ref). The Auto default
/// doesn't need side-table storage (= ComputedSlot default-value
/// tag implies Auto).</para>
///
/// <para><b>Combined grammar per PR-#89 review P2 #4:</b> LineNumber
/// + named ident combine (= <c>foo 2</c> = 2nd occurrence of named
/// line "foo"); Span + count + named ident combine likewise
/// (= <c>span foo 2</c>). The model carries both fields and the
/// <see cref="GridLineKind"/> tag selects which combinations are
/// valid.</para></summary>
internal readonly record struct GridLineValue
{
    public GridLineKind Kind { get; init; }
    /// <summary>For LineNumber: the integer line number (non-zero
    /// per §8.3). For Span: the span count (≥ 1 if present; 0 if
    /// only a name was given). For Auto / NamedLine: 0.</summary>
    public int LineNumber { get; init; }
    /// <summary>For LineNumber / Span: the optional custom-ident
    /// qualifier. For NamedLine: the required name. For Auto:
    /// null.</summary>
    public string? NamedLine { get; init; }

    public static readonly GridLineValue Auto =
        new() { Kind = GridLineKind.Auto, LineNumber = 0, NamedLine = null };

    /// <summary>Bare integer line number (= e.g., <c>2</c>,
    /// <c>-1</c>). Zero rejected per §8.3.</summary>
    public static GridLineValue ForLineNumber(int line)
    {
        if (line == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line),
                "Grid line number 0 is invalid per CSS Grid L1 §8.3");
        }
        return new GridLineValue
        {
            Kind = GridLineKind.LineNumber,
            LineNumber = line,
            NamedLine = null,
        };
    }

    /// <summary>Integer + named-line qualifier (= e.g., <c>foo 2</c>).
    /// Name must be non-empty; integer must be non-zero.</summary>
    public static GridLineValue ForNamedLineNumber(string name, int occurrence)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                "Named-line qualifier cannot be null or empty", nameof(name));
        }
        if (occurrence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence),
                "Grid line occurrence 0 is invalid per CSS Grid L1 §8.3");
        }
        return new GridLineValue
        {
            Kind = GridLineKind.LineNumber,
            LineNumber = occurrence,
            NamedLine = name,
        };
    }

    /// <summary><c>span &lt;integer&gt;</c> (= e.g., <c>span 2</c>).
    /// Count must be ≥ 1 per §8.3.</summary>
    public static GridLineValue ForSpan(int count)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count),
                $"Span count must be ≥ 1 per CSS Grid L1 §8.3; got {count}");
        }
        return new GridLineValue
        {
            Kind = GridLineKind.Span,
            LineNumber = count,
            NamedLine = null,
        };
    }

    /// <summary><c>span &lt;custom-ident&gt;</c> (= e.g.,
    /// <c>span foo</c>; spans to the next occurrence of "foo").
    /// Name must be non-empty.</summary>
    public static GridLineValue ForSpanName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                "Named-line span identifier cannot be null or empty", nameof(name));
        }
        return new GridLineValue
        {
            Kind = GridLineKind.Span,
            LineNumber = 0,  // 0 = "no explicit count; span to next named occurrence"
            NamedLine = name,
        };
    }

    /// <summary><c>span &lt;custom-ident&gt; &lt;integer&gt;</c>
    /// (= e.g., <c>span foo 2</c>; spans 2 occurrences of "foo").
    /// Both required.</summary>
    public static GridLineValue ForSpanNameOccurrence(string name, int count)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                "Named-line span identifier cannot be null or empty", nameof(name));
        }
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count),
                $"Named-span occurrence count must be ≥ 1; got {count}");
        }
        return new GridLineValue
        {
            Kind = GridLineKind.Span,
            LineNumber = count,
            NamedLine = name,
        };
    }

    /// <summary>Bare custom-ident (= e.g., <c>foo</c>; resolves to
    /// the first occurrence of "foo" in the parent's named lines).
    /// Name must be non-empty.</summary>
    public static GridLineValue ForNamedLine(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException(
                "Named-line identifier cannot be null or empty", nameof(name));
        }
        return new GridLineValue
        {
            Kind = GridLineKind.NamedLine,
            LineNumber = 0,
            NamedLine = name,
        };
    }
}

/// <summary>Per CSS Grid L1 §7.7 — flow direction + density modifier
/// for the sparse auto-placement algorithm. Cycle 6 shipped
/// <see cref="Row"/> + <see cref="Column"/>; cycle 7d adds the
/// <c>dense</c> packing modifier per CSS Grid §8.5.
///
/// <para><b>ID encoding</b> matches the cycle-7d keyword resolver
/// table: 0 = row, 1 = column, 2 = row dense, 3 = column dense.
/// The <c>IsDense</c> / <c>IsColumn</c> extension methods on
/// <see cref="GridAutoFlowValueExtensions"/> make the bit semantics
/// explicit at the call site.</para></summary>
internal enum GridAutoFlowValue : byte
{
    /// <summary><c>row</c> (the property default) — items pack along
    /// the inline axis first; sparse mode advances the cursor and
    /// doesn't backtrack to fill earlier holes.</summary>
    Row = 0,

    /// <summary><c>column</c> — items pack along the block axis first;
    /// sparse mode.</summary>
    Column = 1,

    /// <summary>Per Phase 3 Task 18 cycle 7d — <c>row dense</c> (= the
    /// canonical form of bare <c>dense</c>). Dense packing fills
    /// earlier holes left by explicitly-placed items.</summary>
    RowDense = 2,

    /// <summary>Per Phase 3 Task 18 cycle 7d — <c>column dense</c>.</summary>
    ColumnDense = 3,
}

/// <summary>Per Phase 3 Task 18 cycle 7d — extension helpers that
/// decompose <see cref="GridAutoFlowValue"/> into its two
/// independent bits: direction (row vs column) and density (sparse
/// vs dense).</summary>
internal static class GridAutoFlowValueExtensions
{
    /// <summary>True when the dense packing modifier is set
    /// (= <see cref="GridAutoFlowValue.RowDense"/> or
    /// <see cref="GridAutoFlowValue.ColumnDense"/>).</summary>
    public static bool IsDense(this GridAutoFlowValue value)
        => value == GridAutoFlowValue.RowDense
            || value == GridAutoFlowValue.ColumnDense;

    /// <summary>True when the flow direction is column-major
    /// (= <see cref="GridAutoFlowValue.Column"/> or
    /// <see cref="GridAutoFlowValue.ColumnDense"/>).</summary>
    public static bool IsColumn(this GridAutoFlowValue value)
        => value == GridAutoFlowValue.Column
            || value == GridAutoFlowValue.ColumnDense;
}

/// <summary>Per Phase 3 Task 18 cycle 7a + CSS Grid L1 §7.3 — the
/// parsed AST for a <c>grid-template-areas</c> declaration. Stores
/// both the source rectangle of cell names (row-major) AND the
/// derived name → rectangle map so placement-time lookups by area
/// name are O(1).
///
/// <para><b>Validation invariants</b> (per §7.3 + the resolver's
/// validation pass):</para>
/// <list type="bullet">
///   <item><see cref="RowCount"/> ≥ 1 (each row is one CSS string;
///   the empty AST <see cref="None"/> represents the property default
///   <c>none</c>).</item>
///   <item>All rows have the same column count (= ragged rows are
///   rejected at parse time).</item>
///   <item>For each named cell, every occurrence in the grid forms
///   a single rectangle (= same-name cells must be adjacent in both
///   axes; non-rectangular shapes are rejected).</item>
///   <item><c>.</c> tokens are NULL cells (no entry in
///   <see cref="NameToRect"/>).</item>
/// </list>
///
/// <para><b>Coordinate system</b>: <see cref="Cells"/> is indexed
/// <c>[row, column]</c> 0-based. <see cref="GridAreaRect"/>'s
/// <c>RowStart</c> / <c>ColumnStart</c> are 1-based grid LINE
/// numbers (matching the convention used by GridLineValue);
/// <c>RowEnd</c> / <c>ColumnEnd</c> are exclusive (= start + span).</para></summary>
internal sealed record GridTemplateAreas(
    int RowCount,
    int ColumnCount,
    System.Collections.Immutable.ImmutableArray<string?> Cells,
    System.Collections.Immutable.ImmutableDictionary<string, GridAreaRect> NameToRect)
{
    /// <summary>Per CSS Grid L1 §7.3 — the property-default value
    /// (<c>none</c>). No named areas; the placement service treats
    /// any <c>&lt;custom-ident&gt;</c> placement against this as
    /// "unknown name" (= falls back to auto per cycle-6a's existing
    /// approximated-and-fall-to-auto path).</summary>
    public static readonly GridTemplateAreas None = new(
        RowCount: 0,
        ColumnCount: 0,
        Cells: System.Collections.Immutable.ImmutableArray<string?>.Empty,
        NameToRect: System.Collections.Immutable.ImmutableDictionary<string, GridAreaRect>.Empty);

    /// <summary>Indexer for the <c>[row, column]</c> cell name.
    /// Returns <see langword="null"/> for null cells (= <c>.</c>) or
    /// out-of-bounds queries.</summary>
    public string? this[int row, int column]
    {
        get
        {
            if (row < 0 || row >= RowCount || column < 0 || column >= ColumnCount)
                return null;
            return Cells[row * ColumnCount + column];
        }
    }
}

/// <summary>Per Phase 3 Task 18 cycle 7a + CSS Grid L1 §7.3 — the
/// derived rectangle for one named grid area.
///
/// <para>Line numbers are 1-based (matching <see cref="GridLineValue"/>
/// convention). <see cref="RowEnd"/> / <see cref="ColumnEnd"/> are
/// EXCLUSIVE end lines (= the line AFTER the last occupied row /
/// column). So an area spanning rows 1-3 has <c>RowStart=1</c> +
/// <c>RowEnd=4</c> (= span 3).</para></summary>
internal readonly record struct GridAreaRect(
    int RowStart, int RowEnd, int ColumnStart, int ColumnEnd);
