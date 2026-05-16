// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;

namespace NetPdf.Layout.Layouters;

/// <summary>
/// Per Phase 3 Task 7 — extension methods on <see cref="ComputedStyle"/>
/// that decode the typed <see cref="ComputedSlot"/> payloads layouters
/// need (lengths, length-or-auto, percentages). Cycle 1 covers
/// resolved-px lengths only; cycle 3 will add percentage resolution +
/// <c>auto</c> handling per CSS 2.1 §10.3.
///
/// <para><b>Why extension methods.</b> Box layout reads ~10 properties
/// per box (margins, padding, borders, dimensions). Inlining the
/// <c>style.Get(id)</c> + <c>slot.Tag</c> + <c>slot.AsLengthPx()</c>
/// dance at every call site bloats the layouter; extracting it here
/// keeps the layouter focused on geometry math.</para>
///
/// <para><b>Cycle 1 limitations.</b> The methods return 0 for
/// non-LengthPx slot kinds (Auto, Percentage, Unset) — this is the
/// "TODO cycle 3" path. Real layout needs to resolve percentages
/// against the containing-block size + resolve <c>auto</c> per
/// CSS 2.1 §10.3.3. For cycle 1 the implicit model is "all author
/// values are explicit lengths in px" — sufficient to demonstrate
/// the layouter wiring without the percentage-resolution complexity.</para>
/// </summary>
internal static class ComputedStyleLayoutExtensions
{
    /// <summary>Read a length-typed property as CSS px. Returns
    /// <paramref name="defaultPx"/> when the slot is Unset, Auto,
    /// or any non-LengthPx tag (cycle 1 — see class XML doc for
    /// the "TODO cycle 3" handling of Auto + Percentage).</summary>
    public static double ReadLengthPxOrDefault(
        this ComputedStyle style,
        PropertyId id,
        double defaultPx)
    {
        var slot = style.Get(id);
        return slot.Tag == ComputedSlotTag.LengthPx
            ? slot.AsLengthPx()
            : defaultPx;
    }

    /// <summary>Read a length-typed property as CSS px, defaulting to
    /// 0 when the slot is Unset / Auto / non-length. Most box-model
    /// margin/padding/border properties default to 0 per the CSS
    /// initial-value table; this is the convenience overload for that
    /// case.</summary>
    public static double ReadLengthPxOrZero(this ComputedStyle style, PropertyId id) =>
        style.ReadLengthPxOrDefault(id, defaultPx: 0);

    /// <summary>Per Phase 3 Task 8 — read a keyword-typed property as
    /// its keyword index. Returns <paramref name="defaultIndex"/> when
    /// the slot is Unset OR holds a non-Keyword tag (defensive).
    /// Keyword indices are property-specific + match the source-gen'd
    /// table in <c>NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver</c>.
    /// Callers SHOULD wrap raw indices in a typed enum
    /// (<see cref="ReadFloatSide"/>, <see cref="ReadClearKind"/>).</summary>
    public static int ReadKeywordOrDefault(
        this ComputedStyle style,
        PropertyId id,
        int defaultIndex)
    {
        var slot = style.Get(id);
        return slot.Tag == ComputedSlotTag.Keyword
            ? slot.AsKeyword()
            : defaultIndex;
    }

    /// <summary>Per Phase 3 Task 8 — decode <see cref="PropertyId.Float"/>
    /// into a <see cref="FloatSide"/>? (null = <c>none</c>). Keyword
    /// indices: 0=none, 1=left, 2=right, 3=inline-start, 4=inline-end.
    /// Cycle 1 resolves inline-start/end as left/right (LTR-only;
    /// cycle 3 will resolve against writing-mode + direction).</summary>
    public static FloatSide? ReadFloatSide(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.Float, defaultIndex: 0);
        return keyword switch
        {
            1 => FloatSide.Left,
            2 => FloatSide.Right,
            3 => FloatSide.Left,   // inline-start, cycle-1 LTR-only
            4 => FloatSide.Right,  // inline-end,   cycle-1 LTR-only
            _ => null,
        };
    }

    /// <summary>Per Phase 3 Task 8 — decode <see cref="PropertyId.Clear"/>
    /// into a <see cref="ClearKind"/>. Keyword indices: 0=none, 1=left,
    /// 2=right, 3=both, 4=inline-start, 5=inline-end.</summary>
    public static ClearKind ReadClearKind(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.Clear, defaultIndex: 0);
        return keyword switch
        {
            1 => ClearKind.Left,
            2 => ClearKind.Right,
            3 => ClearKind.Both,
            4 => ClearKind.InlineStart,
            5 => ClearKind.InlineEnd,
            _ => ClearKind.None,
        };
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 3 — decode
    /// <see cref="PropertyId.CaptionSide"/> into a <see cref="CaptionSide"/>.
    /// CSS Tables 3 §11.5.2 admits the physical pair <c>top</c> /
    /// <c>bottom</c>; CSS Logical Properties 1 §4.4 admits the
    /// writing-mode-relative pair <c>block-start</c> / <c>block-end</c>
    /// + the inline-axis pair <c>inline-start</c> / <c>inline-end</c>.
    /// Keyword indices match the source-gen'd table in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>:
    /// 0=top, 1=bottom, 2=block-start, 3=block-end, 4=inline-start,
    /// 5=inline-end.
    ///
    /// <para><b>Writing-mode resolution.</b> Sub-cycle 3 maps the
    /// writing-mode-relative keywords assuming LTR + horizontal writing
    /// mode — <c>block-start</c> → <see cref="CaptionSide.Top"/>,
    /// <c>block-end</c> → <see cref="CaptionSide.Bottom"/>. The inline-
    /// axis keywords (<c>inline-start</c> / <c>inline-end</c>) are
    /// authored on table captions only in vertical writing modes; sub-
    /// cycle 3 falls back to <see cref="CaptionSide.Top"/> for them
    /// (RTL + vertical writing-mode support is deferred to sub-cycle 4+
    /// alongside the rest of the writing-mode work; see
    /// <c>docs/deferrals.md#table-auto-fixed-spans-borders</c>).</para>
    /// </summary>
    public static CaptionSide ReadCaptionSide(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.CaptionSide, defaultIndex: 0);
        return keyword switch
        {
            1 => CaptionSide.Bottom,
            2 => CaptionSide.Top,    // block-start → top (LTR horizontal mode)
            3 => CaptionSide.Bottom, // block-end → bottom (LTR horizontal mode)
            // 4 / 5 (inline-start / inline-end) fall through to Top for
            // sub-cycle 3 — vertical writing-mode resolution is deferred.
            _ => CaptionSide.Top,
        };
    }

    /// <summary>Per Phase 3 Task 12 sub-cycle 4 — decode
    /// <see cref="PropertyId.TableLayout"/> into a
    /// <see cref="TableLayoutMode"/>. CSS Tables 3 §3 + §3.5 admit
    /// <c>auto</c> (default — the spec-strict shrink-to-fit min/max-
    /// content algorithm; sub-cycle 5+ work) and <c>fixed</c> (sub-
    /// cycle 4 — column widths derive from <c>&lt;col&gt;</c> +
    /// first-row cell widths). Keyword indices match the source-gen'd
    /// table in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>:
    /// 0=auto, 1=fixed.
    /// </summary>
    public static TableLayoutMode ReadTableLayout(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.TableLayout, defaultIndex: 0);
        return keyword switch
        {
            1 => TableLayoutMode.Fixed,
            _ => TableLayoutMode.Auto,
        };
    }

    /// <summary>Per Phase 3 Task 14 cycle 1 — decode
    /// <see cref="PropertyId.ColumnCount"/> into a positive
    /// integer column count, or <see langword="null"/> when the
    /// property is <c>auto</c> / unset / invalid. Per CSS Multi-column
    /// L1 §3.2 a positive integer specifies the explicit column count
    /// for the multicol container; <c>auto</c> falls back to the
    /// <c>column-width</c>-derived count (sub-cycle 2+ work).
    ///
    /// <para><b>Cycle 1 contract.</b> Returns <see langword="null"/>
    /// when the slot is anything other than <see cref="ComputedSlotTag.Integer"/>
    /// with a value &gt;= 1. That includes <c>auto</c>, unset, parse
    /// failures (the Integer resolver rejected the value), zero, and
    /// negative numbers.</para>
    ///
    /// <para>Per post-PR-#60 review hardening (F#3) — the MulticolLayouter
    /// dispatch in <c>BlockLayouter.IsMulticolContainer</c> now uses a
    /// non-null result &gt;= 1 as the gate (NOT &gt;= 2). CSS Multi-column
    /// L1 §1 says a multicol container is created whenever
    /// <c>column-count</c> (or <c>column-width</c>) is set non-auto;
    /// column boxes establish their own BFC. <c>column-count: 1</c>
    /// reaches <see cref="MulticolLayouter"/> which degrades to
    /// <c>EmitSingleColumnFallthrough</c> — the BFC contract is
    /// preserved without a column-axis split. Pre-fix the gate
    /// required &gt;= 2, so <c>column-count: 1</c> fell through to
    /// ordinary block flow + lost the BFC contract.</para>
    /// </summary>
    public static int? ReadColumnCount(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.ColumnCount);
        if (slot.Tag != ComputedSlotTag.Integer)
        {
            return null;
        }
        var n = slot.AsInteger();
        return n >= 1 ? n : null;
    }

    /// <summary>Per Phase 3 Task 14 cycle 1 — decode
    /// <see cref="PropertyId.ColumnGap"/> into a CSS px length, or
    /// the cycle-1 default (16 px) when the slot is <c>normal</c> /
    /// unset / non-length. Per CSS Multi-column L1 §6.1 the
    /// <c>normal</c> initial value resolves to 1em (≈ 16 px at the
    /// initial font size). Cycle 1 hard-codes 16 px regardless of
    /// font-size; sub-cycle 2+ will resolve against the cascaded
    /// <c>font-size</c>.</summary>
    public static double ReadColumnGap(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.ColumnGap);
        return slot.Tag == ComputedSlotTag.LengthPx
            ? slot.AsLengthPx()
            : 16.0;
    }

    /// <summary>Per Phase 3 Task 14 cycle 4 — decode
    /// <see cref="PropertyId.ColumnWidth"/> as a CSS px length. Returns
    /// <see langword="null"/> when the slot is <c>auto</c> / unset OR
    /// when the slot carries a font-relative length (<c>em</c>,
    /// <c>rem</c>) or percentage — the cycle-1 <c>LengthResolver</c>
    /// returns a <c>ResolutionState.Deferred</c> result for those (the
    /// raw text rides along on the side; the slot itself stays
    /// <see cref="ComputedSlotTag.Unset"/>), and resolving them
    /// requires the font-size cascade which is a sub-cycle 5+ deferral
    /// (see <c>docs/deferrals.md#multicol-balancing-pagination</c>).
    /// Cycle 4 reads only the resolved <see cref="ComputedSlotTag.LengthPx"/>
    /// slot.
    ///
    /// <para><b>Zero handling.</b> Per CSS Multi-column L1 §3.1 the used
    /// value of <c>column-width</c> is <c>max(specifiedValue, 1px)</c>;
    /// authors can write <c>column-width: 0</c> and the spec still
    /// derives columns at the 1px floor. The cycle-1 hardening
    /// <c>NumberResolver</c> rejects NEGATIVE lengths (no slot is
    /// produced) but accepts 0 — this reader returns the resolved value
    /// as-is including 0; the 1px floor is applied by
    /// <see cref="ComputeUsedColumnCount"/>, not here. This keeps the
    /// reader a pure decode + the spec-clamp behavior local to the
    /// derivation helper.</para>
    ///
    /// <para>Used by the multicol dispatch + the column-count derivation
    /// in <see cref="BlockLayouter"/>: when <c>column-count</c> is auto
    /// AND <c>column-width</c> is a length, the effective column count is
    /// <c>floor((containerInline + columnGap) / (usedColumnWidth + columnGap))</c>
    /// per CSS Multi-column L1 §3.3, where
    /// <c>usedColumnWidth = max(columnWidth, 1px)</c>.</para></summary>
    public static double? ReadColumnWidth(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.ColumnWidth);
        return slot.Tag == ComputedSlotTag.LengthPx
            ? slot.AsLengthPx()
            : null;
    }

    /// <summary>Per Phase 3 Task 14 cycle 4 — compute the effective used
    /// column count per CSS Multi-column L1 §3.3. The 4 cases:
    /// <list type="bullet">
    ///   <item><c>column-count: auto</c> + <c>column-width: auto</c> →
    ///   used N = 1 (no multicol dispatch).</item>
    ///   <item><c>column-count: auto</c> + <c>column-width: &lt;length&gt;</c>
    ///   → used N = <c>max(1, floor((containerInline + columnGap) /
    ///   (usedColumnWidth + columnGap)))</c>, where
    ///   <c>usedColumnWidth = max(columnWidth, 1px)</c> per CSS
    ///   Multi-column L1 §3.1.</item>
    ///   <item><c>column-count: &lt;integer&gt;</c> +
    ///   <c>column-width: auto</c> → used N = specified count.</item>
    ///   <item><c>column-count: &lt;integer&gt;</c> +
    ///   <c>column-width: &lt;length&gt;</c> → used N =
    ///   <c>min(specifiedCount, derivedCount)</c>.</item>
    /// </list>
    ///
    /// <para>The container's actual per-column inline-size after N is
    /// chosen continues to follow cycle 1's equal-split formula:
    /// <c>(containerInline - (N-1)*columnGap) / N</c>. Per CSS Multi-
    /// column L1 §3.1 the authored <c>column-width</c> is an OPTIMAL
    /// width — a "tentative used value" — the actual per-column inline-
    /// size computed by the equal-split formula may be wider OR narrower
    /// than the authored value depending on container geometry. Columns
    /// expand to fill the available inline space, so authoring
    /// <c>column-width: 100px</c> in a 400px container with 3 derived
    /// columns produces ~123px columns (not 100px).</para>
    ///
    /// <para><b>Defensive guards.</b> Non-finite container inline-size
    /// produces derivedCount = 1 (no derivable constraint). When both
    /// <paramref name="specifiedColumnCount"/> and
    /// <paramref name="columnWidth"/> are null the result is 1 (no
    /// multicol intent). <paramref name="columnWidth"/> == 0 is admitted
    /// per spec §3.1's 1px-floor clamp (NumberResolver rejects negative
    /// lengths upstream; zero passes through here + clamps to 1px). The
    /// double-precision ratio is clamped to <c>[1, int.MaxValue]</c>
    /// BEFORE the int conversion so an astronomically large container
    /// (e.g., 1e9 px) divided by a 1 px <c>usedColumnWidth</c> + 0 gap
    /// can't trigger undefined behavior in the int cast; the caller's
    /// downstream <c>MaxColumnCount</c> safety cap (=1000 in
    /// MulticolLayouter) then handles the final bound.</para></summary>
    public static int ComputeUsedColumnCount(
        double containerContentInlineSize,
        int? specifiedColumnCount,
        double? columnWidth,
        double columnGap)
    {
        if (specifiedColumnCount is null && columnWidth is null) return 1;

        // `derivedFromWidth` is null when column-width is auto / unset
        // / non-finite / negative — those cases leave derivation
        // unconstrained, and the specifiedColumnCount path takes over
        // exclusively. When derivedFromWidth is non-null it's the
        // CSS Multi-column L1 §3.3 derivation result (clamped to
        // [1, int.MaxValue]).
        int? derivedFromWidth = null;
        // Per post-PR-#60 review hardening (F#1) — admit `columnWidth ==
        // 0` per CSS Multi-column L1 §3.1: the used value clamp is
        // `max(specifiedValue, 1px)` so authors can write
        // `column-width: 0` and the spec still derives columns at the
        // 1px floor. Pre-fix this branch was gated on `cw > 0`, silently
        // routing `column-width: 0` to the "no width constraint" path
        // (= derivedCount = int.MaxValue sentinel) instead of the spec-
        // required derived-N path with the 1px floor. The NumberResolver
        // guard rejects NEGATIVE lengths upstream (no slot is produced)
        // but ALLOWS zero — non-finite values still produce derivedCount
        // = 1.
        if (columnWidth is double cw && double.IsFinite(cw) && cw >= 0)
        {
            // F#1 — clamp to the spec's 1px floor. `usedColumnWidth` is
            // the "used value" in spec terminology; the cascade slot
            // continues to carry the authored value (0).
            var usedColumnWidth = Math.Max(1.0, cw);
            var denom = usedColumnWidth + columnGap;
            if (!double.IsFinite(denom) || denom <= 0)
            {
                derivedFromWidth = 1;
            }
            else
            {
                var numerator = containerContentInlineSize + columnGap;
                if (!double.IsFinite(numerator) || numerator <= 0)
                {
                    derivedFromWidth = 1;
                }
                else
                {
                    // Per post-PR-#60 review hardening (F#4) — clamp the
                    // double ratio BEFORE the int cast. For huge finite
                    // ratios (containerInline=1e15, usedColumnWidth=1px,
                    // columnGap=0 → ratio = 1e15 which exceeds
                    // int.MaxValue ≈ 2.147e9), the pre-fix `(int)` cast
                    // had undefined behavior. The MulticolLayouter's
                    // `MaxColumnCount = 1000` safety cap applies AFTER
                    // this method returns, but it can't repair an
                    // overflowed int conversion. `int.MaxValue` is far
                    // above any sane column count; the layouter then
                    // clamps to its smaller MaxColumnCount.
                    var derivedDouble = Math.Floor(numerator / denom);
                    if (derivedDouble < 1.0) derivedDouble = 1.0;
                    if (derivedDouble > int.MaxValue) derivedDouble = int.MaxValue;
                    derivedFromWidth = (int)derivedDouble;
                }
            }
        }
        // Else: column-width is null OR non-finite OR negative (the
        // negative case is defensive: NumberResolver should never
        // produce a negative LengthPx slot). The derivation is left
        // unconstrained — derivedFromWidth stays null + specifiedColumnCount
        // wins exclusively.

        if (specifiedColumnCount is int sc && sc >= 1)
        {
            return derivedFromWidth is int derivedW ? Math.Min(sc, derivedW) : sc;
        }
        // No specified count — pass through the derivation, or default
        // to 1 when only the specifiedColumnCount path applied + was
        // null/invalid (= the first-line guard above already returned
        // for the all-null case; this fallback is defensive).
        return derivedFromWidth ?? 1;
    }

    /// <summary>Per Phase 3 Task 14 cycle 3 — decode
    /// <see cref="PropertyId.ColumnFill"/> into a
    /// <see cref="ColumnFillValue"/>. Per CSS Multi-column L1 §3.4
    /// the property admits three keywords: <c>balance</c> (the spec
    /// default — columns should have approximately equal block-axis
    /// extent), <c>balance-all</c> (same as <c>balance</c> but applies
    /// to all fragmentainers, not just the last — see deferrals doc
    /// for the last-fragmentainer special-case which is sub-cycle 2+
    /// scope), and <c>auto</c> (serial fill, left-to-right, no
    /// balancing — the cycle 1+2 behavior).
    ///
    /// <para>Keyword indices match the source-gen'd table in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>:
    /// 0=balance, 1=balance-all, 2=auto. The spec default (= the
    /// initial value when the slot is unset or non-keyword) is
    /// <see cref="ColumnFillValue.Balance"/>.</para></summary>
    public static ColumnFillValue ReadColumnFill(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.ColumnFill, defaultIndex: 0);
        return keyword switch
        {
            1 => ColumnFillValue.BalanceAll,
            2 => ColumnFillValue.Auto,
            _ => ColumnFillValue.Balance,
        };
    }

    /// <summary>Per Phase 3 Task 15 L2 — decode
    /// <see cref="PropertyId.JustifyContent"/> per CSS Box Alignment L3
    /// §4.5. L2 ships the common <c>&lt;content-position&gt;</c> +
    /// <c>&lt;content-distribution&gt;</c> values; logical-axis aliases
    /// (<c>start</c> / <c>end</c> / <c>left</c> / <c>right</c>) map to
    /// <c>flex-start</c> / <c>flex-end</c> for the L1 default
    /// <c>flex-direction: row</c> (writing-mode-aware mapping is L3+
    /// scope). The <c>safe</c> / <c>unsafe</c> overflow-position
    /// modifiers (= compound keywords like <c>safe center</c>) are
    /// treated as the bare position for L2 — the spec's safe-mode
    /// overflow containment (= prevent items being pushed offscreen
    /// when overflowing) is L3+ scope.
    ///
    /// <para><b>Keyword index mapping.</b> The source-gen'd
    /// <c>BuildJustifyContentTable</c> in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// emits indices in this order: 0=normal, 1=space-between,
    /// 2=space-around, 3=space-evenly, 4=stretch, 5=center, 6=start,
    /// 7=end, 8=flex-start, 9=flex-end, 10=left, 11=right, 12-18=safe
    /// {center, start, end, flex-start, flex-end, left, right},
    /// 19-25=unsafe {…same 7…}. The <c>safe</c> / <c>unsafe</c>
    /// compound indices (12+) all fall through to <see cref="JustifyContentValue.FlexStart"/>
    /// for L2; the bare-position semantics are captured when authors
    /// use the unmodified position keyword.</para>
    ///
    /// <para><b>Spec mapping notes.</b> <c>normal</c> resolves to
    /// <c>flex-start</c> per CSS Flexbox L1 §8.2 (the flex container's
    /// computed default). <c>stretch</c> is the grid default; for flex
    /// containers it has no effect on main-axis packing per spec, so
    /// L2 maps it to <c>flex-start</c>. The logical aliases
    /// <c>start</c> / <c>end</c> + the directional aliases <c>left</c> /
    /// <c>right</c> map to <c>flex-start</c> / <c>flex-end</c> under
    /// the L1 default LTR + <c>flex-direction: row</c>.</para></summary>
    public static JustifyContentValue ReadJustifyContent(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.JustifyContent, defaultIndex: 0);
        return keyword switch
        {
            // <content-distribution>
            1 => JustifyContentValue.SpaceBetween,
            2 => JustifyContentValue.SpaceAround,
            3 => JustifyContentValue.SpaceEvenly,
            4 => JustifyContentValue.FlexStart,       // stretch → flex-start for flex per spec
            // <content-position>
            5 => JustifyContentValue.Center,
            6 => JustifyContentValue.FlexStart,       // start → flex-start (LTR row)
            7 => JustifyContentValue.FlexEnd,         // end → flex-end (LTR row)
            8 => JustifyContentValue.FlexStart,
            9 => JustifyContentValue.FlexEnd,
            10 => JustifyContentValue.FlexStart,      // left → flex-start (row)
            11 => JustifyContentValue.FlexEnd,        // right → flex-end (row)
            // safe / unsafe <position> compounds (indices 12+) — L2
            // treats them as flex-start; the overflow modifier's safe-
            // mode containment is L3+ scope. The bare-position semantics
            // are captured when authors omit the overflow modifier.
            _ => JustifyContentValue.FlexStart,       // 0 (normal) → flex-start; unknown → flex-start
        };
    }

    /// <summary>Per Phase 3 Task 14 cycle 3 + post-PR-#59 review
    /// hardening (Finding #7) — predicate distinguishing <c>height:
    /// auto</c> from any EXPLICIT sizing on a box's computed style.
    /// Returns <see langword="true"/> only when the height slot is
    /// <see cref="ComputedSlotTag.Unset"/> (= the default <c>auto</c>)
    /// OR <see cref="ComputedSlotTag.Keyword"/> (= the explicit
    /// <c>auto</c> keyword).
    ///
    /// <para><b>Pre-hardening bug.</b> The original predicate returned
    /// <c>slot.Tag != ComputedSlotTag.LengthPx</c>, which incorrectly
    /// reported <c>height: 50%</c> (Percentage) and <c>height: calc(...)</c>
    /// (Calc) as auto. Per CSS 2.1 §10.5 a percentage height resolves
    /// against the containing block's height — that's EXPLICIT sizing,
    /// not auto. Routing percentage-height multicols into the balancing
    /// path would over-shrink columns + drop content out of the
    /// container.</para>
    ///
    /// <para>Mirrors <c>BlockLayouter.IsHeightAuto</c> (private
    /// instance method) but exposed at the extension layer so
    /// <see cref="MulticolLayouter"/> can gate its
    /// <c>column-fill: balance</c> activation on the same predicate
    /// without taking a dependency on BlockLayouter's
    /// instance.</para></summary>
    public static bool IsHeightAuto(this Boxes.Box box)
    {
        var slot = box.Style.Get(PropertyId.Height);
        // Height is type LengthPercentageAuto. Only the `auto` keyword OR
        // unset (= default `auto`) are auto. Percentage values are
        // explicit sizing relative to the containing block; LengthPx is
        // explicit absolute sizing. Per CSS 2.1 §10.5 percentage height
        // resolves against the containing block's height; treating it as
        // auto would route balanced multicol into the wrong layout path.
        return slot.Tag is ComputedSlotTag.Unset or ComputedSlotTag.Keyword;
    }
}

/// <summary>Per Phase 3 Task 12 sub-cycle 3 — typed decode of
/// <see cref="PropertyId.CaptionSide"/>. CSS Tables 3 §11.5.2 admits
/// the physical pair <c>top</c> / <c>bottom</c>; CSS Logical Properties
/// 1 §4.4 admits the writing-mode-relative <c>block-start</c> /
/// <c>block-end</c> pair which sub-cycle 3 maps to the same two
/// physical sides under LTR horizontal writing mode (RTL + vertical
/// modes deferred).</summary>
internal enum CaptionSide : byte
{
    Top = 0,
    Bottom = 1,
}

/// <summary>Per Phase 3 Task 12 sub-cycle 4 — typed decode of
/// <see cref="PropertyId.TableLayout"/>. CSS Tables 3 §3 + §3.5
/// admit two values; sub-cycle 4 ships <see cref="Fixed"/> in full
/// + leaves <see cref="Auto"/> using the equal-split approximation
/// pending sub-cycle 5+ shrink-to-fit work.</summary>
internal enum TableLayoutMode : byte
{
    Auto = 0,
    Fixed = 1,
}

/// <summary>Per Phase 3 Task 14 cycle 3 — typed decode of
/// <see cref="PropertyId.ColumnFill"/>. CSS Multi-column L1 §3.4
/// admits <c>balance</c> (the spec default — columns should have
/// approximately equal block-axis extent on every fragmentainer
/// except the last), <c>balance-all</c> (same behavior on EVERY
/// fragmentainer, including the last — sub-cycle 2+ scope; cycle 3
/// treats this identically to <c>balance</c>), and <c>auto</c> (no
/// balancing — columns fill serially left-to-right, the cycle 1+2
/// behavior).
///
/// <para>Cycle 3 only activates balancing for
/// <c>column-fill: balance</c> (or <c>balance-all</c>) AND
/// <c>height: auto</c>. Explicit-height containers use the cycle 1+2
/// serial-fill path regardless of <c>column-fill</c> — matches the
/// conservative Prince / WeasyPrint behavior + avoids the
/// over-shrinking that would otherwise drop content out of a fixed-
/// height container.</para></summary>
internal enum ColumnFillValue : byte
{
    Balance = 0,
    BalanceAll = 1,
    Auto = 2,
}

/// <summary>Per Phase 3 Task 15 L2 — typed decode of
/// <see cref="PropertyId.JustifyContent"/>. CSS Box Alignment L3 §4.5
/// admits a large grammar (<c>normal | &lt;content-distribution&gt; |
/// [&lt;overflow-position&gt;? &amp;&amp; [&lt;content-position&gt; |
/// left | right]]</c>); L2 collapses this to six effective behaviors
/// covering the common flexbox main-axis alignment patterns. Logical-
/// axis aliases (<c>start</c> / <c>end</c>) and directional aliases
/// (<c>left</c> / <c>right</c>) map to <see cref="FlexStart"/> /
/// <see cref="FlexEnd"/> under the L1 default LTR +
/// <c>flex-direction: row</c>; writing-mode-aware mapping is L3+
/// scope. The <c>safe</c> / <c>unsafe</c> overflow modifiers are
/// dropped by the decode (compound indices map to <see cref="FlexStart"/>
/// in L2); safe-mode overflow containment is L3+ scope.</summary>
internal enum JustifyContentValue : byte
{
    FlexStart = 0,
    FlexEnd = 1,
    Center = 2,
    SpaceBetween = 3,
    SpaceAround = 4,
    SpaceEvenly = 5,
}
