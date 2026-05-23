// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// Per Phase 3 Task 17 cycle 0 — CSS Grid L1 typed value model. Covers
/// the AST shapes for <c>grid-template-rows</c> / <c>grid-template-columns</c>
/// (= <see cref="TrackList"/>) and <c>grid-row-start</c> / <c>-end</c> /
/// <c>grid-column-start</c> / <c>-end</c> (= <see cref="GridLineValue"/>).
///
/// <para><b>Design constraint per PR-#88 review P2 #4:</b> a record
/// struct can't be self-referential in C# (= compile error from a
/// <c>readonly record struct X(X? Other)</c> shape). The track-AST
/// uses a flat discriminated payload (= one struct with all possible
/// payload fields; the <see cref="GridTrackKind"/> tag selects which
/// fields are read). <c>minmax()</c>'s sub-args are inline (= the
/// MinSub* + MaxSub* fields on <see cref="TrackEntry"/>) so no inner
/// recursion is needed.</para>
///
/// <para><b>Design constraint per PR-#88 review P2 #6:</b> the AST
/// is stored as parsed (= preserving named lines, <c>repeat()</c>
/// groups, and <c>auto-fill</c> / <c>auto-fit</c> markers).
/// Layout-time expansion in <c>GridLayouter</c> resolves
/// <c>auto-fill</c> / <c>auto-fit</c> counts against the container
/// size. <c>repeat(&lt;integer&gt;, ...)</c> with a literal integer
/// count is constant-folded at PARSE time since it doesn't depend
/// on layout context.</para>
/// </summary>

/// <summary>Per CSS Grid L1 §7.2 — the kind of one entry in a track
/// list. Determines which payload fields of <see cref="TrackEntry"/>
/// are read.</summary>
internal enum GridTrackKind : byte
{
    /// <summary><c>&lt;length&gt;</c> track — value in
    /// <see cref="TrackEntry.LengthPx"/>.</summary>
    LengthPx = 0,

    /// <summary><c>&lt;flex&gt;</c> track (e.g., <c>1fr</c>) — value
    /// in <see cref="TrackEntry.FrValue"/>. Cycle 2 ships the §11.7
    /// "Find the Size of an fr" algorithm.</summary>
    Fr = 1,

    /// <summary><c>auto</c> / <c>min-content</c> / <c>max-content</c>
    /// — cycle 3 ships intrinsic-sizing with the L19-content-measurement
    /// approximation (= contribution = item's declared dimension or 0).
    /// The three keywords resolve identically under the approximation;
    /// full distinction lands when L19 ships.</summary>
    Auto = 2,

    /// <summary><c>minmax(min, max)</c> — sub-args in
    /// <see cref="TrackEntry.MinSubKind"/> + <see cref="TrackEntry.MaxSubKind"/>
    /// + the corresponding LengthPx / FrValue fields. Per CSS Grid §7.2.4,
    /// the <c>min</c> arg can be <c>&lt;length&gt;</c> / <c>auto</c> /
    /// <c>min-content</c> / <c>max-content</c> (= Fr is INVALID in min);
    /// the <c>max</c> arg can additionally be Fr.</summary>
    MinMax = 3,

    /// <summary><c>fit-content(limit)</c> — limit in
    /// <see cref="TrackEntry.LengthPx"/>. Per CSS Grid §7.2.2 the
    /// effective size formula is
    /// <c>max(auto-minimum, min(limit, max-content))</c>; the cycle-3
    /// approximation makes <c>auto-minimum</c> = 0 + <c>max-content</c>
    /// = item's declared dimension.</summary>
    FitContent = 4,
}

/// <summary>One entry in a parsed track list. The struct is flat
/// (= zero recursion) so it fits the C# record-struct constraint
/// per PR-#88 review P2 #4. The <see cref="Kind"/> tag selects
/// which payload fields are read; unused fields are zero.
///
/// <para><b>Layout iteration:</b> the GridLayouter iterates
/// <c>TrackList.Items</c> (= a mix of inline TrackEntry +
/// repeat-group references); the <c>repeat()</c> groups expand at
/// layout time. Final track positions = cumulative sum after
/// expansion + track-sizing algorithm resolution.</para></summary>
internal readonly record struct TrackEntry(
    GridTrackKind Kind,
    double LengthPx,        // For LengthPx + FitContent (the limit).
    double FrValue,         // For Fr.
    GridTrackKind MinSubKind,    // For MinMax: the min arg's kind.
    double MinSubLengthPx,       //   Only LengthPx + Auto valid per §7.2.4.
    GridTrackKind MaxSubKind,    // For MinMax: the max arg's kind.
    double MaxSubLengthPx,       //   LengthPx + Auto + Fr all valid.
    double MaxSubFrValue);

/// <summary>One <c>repeat()</c> group in a parsed track list. May
/// expand to multiple <see cref="TrackEntry"/> at layout time.
/// <para><see cref="Count"/> encoding:
/// <list type="bullet">
///   <item>Positive integer — explicit count (constant-folded at parse
///   time; cycle 4 ships this form).</item>
///   <item><c>0</c> — <c>auto-fill</c> (cycle 7; count derived from
///   container size at layout time).</item>
///   <item><c>-1</c> — <c>auto-fit</c> (cycle 7; like auto-fill but
///   empty tracks collapse).</item>
/// </list></para>
/// The <see cref="Pattern"/> array preserves the per-repeat sub-track
/// shape; named lines inside repeat groups are stored as
/// <see cref="TrackListNamedLine"/> entries within the parent's
/// <see cref="TrackList.Items"/> (= not nested under TrackRepeat
/// since repeat() is its own list-level item).</summary>
internal sealed record TrackRepeat(
    int Count,
    ImmutableArray<TrackEntry> Pattern);

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

/// <summary>A named line (= <c>[name]</c> in CSS syntax). Cycle 7
/// reads these for <c>grid-row-start: &lt;name&gt;</c> resolution.
/// Multiple named lines at the same position are stored as separate
/// entries; the cycle-7 resolver concatenates them.</summary>
internal sealed record TrackListNamedLine(string Name) : TrackListItem;

/// <summary>Per CSS Grid L1 §8.3 — the kind of a grid-line value
/// (= the value of <c>grid-row-start</c> / <c>-end</c> /
/// <c>grid-column-start</c> / <c>-end</c>).</summary>
internal enum GridLineKind : byte
{
    /// <summary><c>auto</c> — the default per §8.3. Placement is
    /// resolved at layout time (= auto-placement algorithm).</summary>
    Auto = 0,

    /// <summary><c>&lt;integer&gt;</c> line number. Negative numbers
    /// count from the explicit grid's end (= -1 is the last line).
    /// Positive numbers count from the start. Zero is invalid per
    /// §8.3 (parser rejects it).</summary>
    LineNumber = 1,

    /// <summary><c>span &lt;integer&gt;</c> — the item spans N tracks
    /// in the specified direction. <see cref="GridLineValue.LineNumber"/>
    /// stores the span count (always positive).</summary>
    Span = 2,

    /// <summary><c>&lt;custom-ident&gt;</c> — a named line. Cycle 7
    /// ships named-line lookup against
    /// <c>grid-template-areas</c> + the named lines in the
    /// <see cref="TrackList"/>.</summary>
    NamedLine = 3,
}

/// <summary>A grid-line value (= the value of one of the four
/// grid-row-start / -end / grid-column-start / -end longhands).
/// Trivially flat (= no recursion) per PR-#88 review P2 #4.
///
/// <para><b>ComputedSlot storage</b>: small enough to fit inline
/// without the side-table pattern. The cycle 0 source-gen wires
/// the <c>PropertyType.GridLine</c> tag to a dedicated
/// resolver + reader pair.</para></summary>
internal readonly record struct GridLineValue(
    GridLineKind Kind,
    int LineNumber,        // For LineNumber + Span (= span count).
    string? NamedLine)     // For NamedLine; null otherwise.
{
    /// <summary>The CSS property-default value (= <c>auto</c>).</summary>
    public static readonly GridLineValue Auto =
        new(GridLineKind.Auto, 0, null);
}
