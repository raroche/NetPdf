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
    /// case.
    ///
    /// <para><b>Border-width used-value gate.</b> For the four
    /// <c>border-*-width</c> properties this applies CSS Backgrounds &amp;
    /// Borders 3 §4.3: the USED border width is 0 when the corresponding
    /// <c>border-*-style</c> is <c>none</c> or <c>hidden</c>, regardless of
    /// the declared (computed) width. <c>border-*-width</c> resolves to its
    /// nominal px (initial <c>medium</c> = 3px) in the cascade; gating here —
    /// the single reader every layout border-width call site funnels through —
    /// means an unbordered box (the default <c>border-style: none</c>) reserves
    /// no border space, so resolving border-width doesn't grow every box.</para></summary>
    public static double ReadLengthPxOrZero(this ComputedStyle style, PropertyId id)
    {
        if (TryBorderStyleFor(id, out var styleId)
            && style.ReadKeywordOrDefault(styleId, defaultIndex: 0) <= 1) // 0=none, 1=hidden
        {
            return 0;
        }
        return style.ReadLengthPxOrDefault(id, defaultPx: 0);
    }

    /// <summary>line-height cycle (CSS 2.2 §10.8.1 / CSS Inline 3) — the USED <c>line-height</c> in px,
    /// or <see langword="null"/> when it's <c>normal</c> / unset (the caller then uses its own
    /// <paramref name="fontSizePx"/> × 1.2 default). Decodes the full computed grammar that
    /// <c>LineHeightResolver</c> produces: a <c>LengthPx</c> slot (an absolute <c>&lt;length&gt;</c>, or an
    /// <c>em</c>/<c>rem</c> already folded by <c>DeferredLengthResolver</c>) → that px; a <c>Number</c>
    /// slot (a unitless <c>&lt;number&gt;</c> multiplier) → number × <paramref name="fontSizePx"/> (the
    /// element's OWN font-size — a number inherits AS the number); a <c>Percentage</c> slot → % of
    /// <paramref name="fontSizePx"/>; <c>normal</c> / a keyword / an unset slot → <see langword="null"/>.
    /// <para><b>Explicit zero.</b> A valid <c>line-height: 0</c> / <c>0px</c> / <c>0%</c> returns
    /// <c>0.0</c> — DISTINCT from <c>normal</c> (null) — so a collapsed line box is honored instead of
    /// silently falling back to the default (post-PR-#197 review P2; a plain numeric sentinel can't tell
    /// explicit zero from "use the default"). Pre-fix every call site read
    /// <c>ReadLengthPxOrZero(LineHeight)</c>, which only honored a <c>LengthPx</c> slot AND the dispatch
    /// never produced one — so a declared length silently became <c>font-size × 1.2</c>.</para></summary>
    public static double? ReadLineHeightPx(this ComputedStyle style, double fontSizePx)
    {
        var slot = style.Get(PropertyId.LineHeight);
        return slot.Tag switch
        {
            ComputedSlotTag.LengthPx => slot.AsLengthPx(),
            ComputedSlotTag.Number => slot.AsNumber() * fontSizePx,
            ComputedSlotTag.Percentage => slot.AsPercentage() / 100.0 * fontSizePx,
            _ => null,   // normal / Keyword(normal) / Unset → caller's font-size × 1.2 default
        };
    }

    /// <summary>Body % lengths (body-percent cycle) — like
    /// <see cref="ReadLengthPxOrZero(ComputedStyle, PropertyId)"/> but a PERCENTAGE slot resolves
    /// against <paramref name="containingInlinePx"/> (the containing block's INLINE size: CSS 2.2
    /// §8.3/§8.4 resolve margin/padding percentages on EVERY side against the inline axis, §10.2
    /// width likewise). A negative containing size reads as 0 (defensive). `auto`/keyword/deferred
    /// slots still read 0 (the cycle-1 contract).</summary>
    public static double ReadLengthOrPercentPx(
        this ComputedStyle style, PropertyId id, double containingInlinePx)
    {
        var slot = style.Get(id);
        if (slot.Tag == ComputedSlotTag.Percentage)
            return slot.AsPercentage() / 100.0 * Math.Max(0, containingInlinePx);
        return style.ReadLengthPxOrZero(id);
    }

    /// <summary>Body % lengths (body-percent cycle) — rewrite a PERCENTAGE padding slot into its
    /// USED px (against the containing block's inline size, §8.4) IN PLACE, so every downstream
    /// reader agrees with layout: <c>TextPainter</c>'s content-origin inset and
    /// <c>FragmentPainter</c> read the slots with <see cref="ReadLengthPxOrZero(ComputedStyle, PropertyId)"/>
    /// at paint time, where the containing size is long gone (the same used-value-rewrite pattern
    /// as the margin-box painter's <c>ResolveUsedPaddingInPlace</c>). Margins/width need no
    /// rewrite — they are layout-only inputs. Idempotent (the slot becomes LengthPx).</summary>
    public static void ResolveUsedPercentPaddingInPlace(
        this ComputedStyle style, double containingInlinePx)
    {
        ReadOnlySpan<PropertyId> paddings =
            [PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft];
        foreach (var id in paddings)
        {
            var slot = style.Get(id);
            if (slot.Tag == ComputedSlotTag.Percentage)
            {
                style.Set(id, ComputedSlot.FromLengthPx(
                    slot.AsPercentage() / 100.0 * Math.Max(0, containingInlinePx)));
            }
        }
    }

    /// <summary>Flex box-sizing / content-inset cycle — the USED border + padding on the
    /// INLINE axis (left + right), i.e. the chrome between the border box and the content
    /// box horizontally. Border widths are §4.3-gated by their style (an unbordered side
    /// reads 0) because they funnel through <see cref="ReadLengthPxOrZero(ComputedStyle, PropertyId)"/>;
    /// percentage padding reads 0 (a documented flex-item approximation, matching the
    /// row-flex pre-measure). Assumes horizontal-tb LTR (the flex emission's physical
    /// mapping; writing-mode / RTL is a tracked approximation).</summary>
    public static double InlineBorderPaddingPx(this ComputedStyle s) =>
        s.ReadLengthPxOrZero(PropertyId.BorderLeftWidth) + s.ReadLengthPxOrZero(PropertyId.PaddingLeft)
        + s.ReadLengthPxOrZero(PropertyId.PaddingRight) + s.ReadLengthPxOrZero(PropertyId.BorderRightWidth);

    /// <summary>Flex box-sizing / content-inset cycle — the USED border + padding on the
    /// BLOCK axis (top + bottom). See <see cref="InlineBorderPaddingPx"/>.</summary>
    public static double BlockBorderPaddingPx(this ComputedStyle s) =>
        s.ReadLengthPxOrZero(PropertyId.BorderTopWidth) + s.ReadLengthPxOrZero(PropertyId.PaddingTop)
        + s.ReadLengthPxOrZero(PropertyId.PaddingBottom) + s.ReadLengthPxOrZero(PropertyId.BorderBottomWidth);

    /// <summary>Flex box-sizing / content-inset cycle — the inline-START border + padding
    /// (left), the offset from a box's border-box origin to its content-box origin on the
    /// inline axis. Used to inset a flex item's flushed content.</summary>
    public static double InlineStartBorderPaddingPx(this ComputedStyle s) =>
        s.ReadLengthPxOrZero(PropertyId.BorderLeftWidth) + s.ReadLengthPxOrZero(PropertyId.PaddingLeft);

    /// <summary>Flex box-sizing / content-inset cycle — the block-START border + padding
    /// (top). See <see cref="InlineStartBorderPaddingPx"/>.</summary>
    public static double BlockStartBorderPaddingPx(this ComputedStyle s) =>
        s.ReadLengthPxOrZero(PropertyId.BorderTopWidth) + s.ReadLengthPxOrZero(PropertyId.PaddingTop);

    /// <summary>Flex box-sizing / content-inset cycle — the USED border + padding on the
    /// axis a direction-resolved size property measures: <see cref="PropertyId.Width"/> /
    /// <see cref="PropertyId.MinWidth"/> / <see cref="PropertyId.MaxWidth"/> → the inline
    /// axis; any other (the height family) → the block axis. Lets the §9.7 main-size /
    /// min-max readers derive their own box-sizing chrome from the property id without a
    /// signature change.</summary>
    public static double AxisBorderPaddingPx(this ComputedStyle s, PropertyId sizeProperty) =>
        sizeProperty is PropertyId.Width or PropertyId.MinWidth or PropertyId.MaxWidth
            ? s.InlineBorderPaddingPx()
            : s.BlockBorderPaddingPx();

    /// <summary>Flex box-sizing cycle — a flex item's BORDER-box CROSS size for a
    /// direction-resolved cross-size property: a DEFINITE <c>LengthPx</c> (incl. an explicit 0)
    /// mapped through <see cref="BoxSizingHelper.DeclaredToBorderBox"/> (honoring box-sizing);
    /// <c>auto</c> / percentage / unset → 0 (stretch or content sizes it). Gating on the slot
    /// TAG (post-PR-#190 Copilot review) keeps an UNRESOLVED percentage cross-size at 0 rather
    /// than flooring it to the chrome — so the emission cross-size read AND
    /// <c>FlexLinePacker.CrossBorderBoxSize</c> (line packing) AGREE.</summary>
    public static double CrossBorderBoxSizePx(this ComputedStyle s, PropertyId crossSizeProperty)
    {
        var slot = s.Get(crossSizeProperty);
        return slot.Tag == ComputedSlotTag.LengthPx
            ? BoxSizingHelper.DeclaredToBorderBox(
                s, Math.Max(0, slot.AsLengthPx()), s.AxisBorderPaddingPx(crossSizeProperty))
            : 0.0;
    }

    /// <summary>Body text-align cycle — the horizontal line-alignment FACTOR (CSS Text 3 §7.1)
    /// for inline content: the fraction of a line's free space (content width − line advance) the
    /// line shifts by. <c>center</c> → 0.5; physical <c>right</c> → 1.0, physical <c>left</c> → 0.
    /// <c>justify</c> / <c>justify-all</c> return 0 HERE — they distribute inter-word (not a
    /// whole-line shift) via <see cref="ReadInlineJustify"/>, falling back to start for the
    /// non-justified last line.
    ///
    /// <para><b>Direction-relative start/end (direction pipeline).</b> <c>start</c> / <c>end</c>
    /// resolve against the box's computed <c>direction</c> (<see cref="DirectionStyleExtensions.IsRtl"/>):
    /// in LTR <c>start</c> → 0 (left), <c>end</c> → 1 (right); in RTL the start edge is the RIGHT
    /// edge, so <c>start</c> → 1 and <c>end</c> → 0. The initial <c>text-align: start</c> therefore
    /// RIGHT-aligns an RTL block. An LTR box is byte-identical to the pre-pipeline mapping.</para>
    ///
    /// <para><b><c>match-parent</c> is a DEFERRED approximation</b> — a fixed physical LEFT (factor 0),
    /// direction-INSENSITIVE. Spec-correct <c>match-parent</c> (CSS Text 3 §7.1) takes the PARENT's
    /// <c>text-align</c>, resolves a <c>start</c>/<c>end</c> against the PARENT's <c>direction</c>, and
    /// inherits that matched alignment — which needs the parent computed style at cascade time, not
    /// available to this layout-time reader. Carved out of the direction-aware <c>start</c> path so it
    /// does NOT masquerade as spec-correct. See <c>deferrals.md#text-align-match-parent</c>.</para>
    ///
    /// <para>Consumed by <c>TextPainter</c> (the glyph lines, via <c>BoxFragment.LineAlignFactor</c>)
    /// + the inline-atomic placement, so the glyphs AND any inline atomic shift together — including
    /// to the right under an RTL <c>start</c>.</para></summary>
    public static double ReadInlineAlignFactor(this ComputedStyle s)
    {
        var rtl = s.IsRtl();
        return s.ReadKeywordOrDefault(PropertyId.TextAlign, defaultIndex: 0) switch
        {
            2 => 0.0,              // left  (physical)
            3 => 1.0,              // right (physical)
            4 => 0.5,              // center
            5 or 7 => 0.0,         // justify / justify-all — distributed, not a whole-line shift
            1 => rtl ? 0.0 : 1.0,  // end   → left in RTL, right in LTR
            6 => 0.0,              // match-parent — DEFERRED fixed approximation (left); see XML doc
            _ => rtl ? 1.0 : 0.0,  // start(0) → right in RTL, left in LTR
        };
    }

    /// <summary>text-align: justify cycle — whether inline content should be JUSTIFIED (CSS Text 3
    /// §7.3 inter-word distribution): the line's free space (content width − line advance) is spread
    /// across its inter-word gaps. True for <c>justify</c>(5) and <c>justify-all</c>(7) — both share the
    /// inter-word distribution; <see cref="ReadInlineJustifyAll"/> adds the <c>justify-all</c> distinction
    /// (the LAST line justifies too). Consumed by <c>TextPainter</c> (splits each justified line's glyphs
    /// at word-separator spaces and adds per-gap advance) + the inline-atomic placement (shifts an atomic
    /// by the gaps before it). Mutually exclusive with <see cref="ReadInlineAlignFactor"/> (which returns
    /// 0 for justify, so a non-justified line — e.g. the plain-justify last line — falls back to start).</summary>
    public static bool ReadInlineJustify(this ComputedStyle s) =>
        s.ReadKeywordOrDefault(PropertyId.TextAlign, defaultIndex: 0) is 5 or 7;

    /// <summary>text-align: justify cycle — whether the LAST line justifies too (CSS Text 3 §7.3) —
    /// i.e. <c>text-align: justify-all</c>(7). <c>justify</c>(5) leaves the last line start-aligned (the
    /// §7.3 exception); <c>justify-all</c> justifies every line including the last. Lets <c>TextPainter</c>
    /// (and the inline-atomic placement) lift the last-line gate. Under justify-all an INTERNAL
    /// forced-break (<c>&lt;br&gt;</c>) line justifies too (PR-3 task 9 — the §7.3 forced-break exception is
    /// lifted); the gate is keyed on the block's genuine LAST line (<c>isLastLine</c>), NOT the
    /// mandatory-break flag, since a block's final line also carries that flag.</summary>
    public static bool ReadInlineJustifyAll(this ComputedStyle s) =>
        s.ReadKeywordOrDefault(PropertyId.TextAlign, defaultIndex: 0) is 7;

    /// <summary>Maps a <c>border-*-width</c> PropertyId to its sibling
    /// <c>border-*-style</c> PropertyId for the §4.3 used-width style gate;
    /// returns <see langword="false"/> for any other property.</summary>
    private static bool TryBorderStyleFor(PropertyId widthId, out PropertyId styleId)
    {
        switch (widthId)
        {
            case PropertyId.BorderTopWidth: styleId = PropertyId.BorderTopStyle; return true;
            case PropertyId.BorderRightWidth: styleId = PropertyId.BorderRightStyle; return true;
            case PropertyId.BorderBottomWidth: styleId = PropertyId.BorderBottomStyle; return true;
            case PropertyId.BorderLeftWidth: styleId = PropertyId.BorderLeftStyle; return true;
            default: styleId = default; return false;
        }
    }

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

    /// <summary>Per Phase 3 Task 19 cycle 1 — decode
    /// <see cref="PropertyId.Position"/> into a <see cref="PositionValue"/>.
    /// Keyword indices match the source-gen'd table in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// (<c>T("static", "relative", "absolute", "fixed", "sticky")</c>):
    /// 0=static (default), 1=relative, 2=absolute, 3=fixed, 4=sticky.
    /// Unset / non-keyword slots fall back to
    /// <see cref="PositionValue.Static"/> (= the CSS Positioned Layout
    /// L3 §3 initial value).</summary>
    public static PositionValue ReadPosition(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.Position, defaultIndex: 0);
        return keyword switch
        {
            1 => PositionValue.Relative,
            2 => PositionValue.Absolute,
            3 => PositionValue.Fixed,
            4 => PositionValue.Sticky,
            _ => PositionValue.Static,
        };
    }

    /// <summary>Per Phase 3 Task 19 cycle 1 — <see langword="true"/>
    /// when the box is <c>position: absolute</c> specifically (removed
    /// from normal flow + positioned against its nearest positioned
    /// ancestor / ICB per CSS Positioned Layout L3 §6). Distinct from
    /// <see cref="IsFixedPositioned"/>: both are out-of-flow (see
    /// <see cref="IsOutOfFlow"/>), but only this one keys the
    /// nearest-positioned-ancestor containing-block walk; fixed always
    /// anchors to the page / ICB.</summary>
    public static bool IsAbsolutelyPositioned(this ComputedStyle style)
        => style.ReadPosition() == PositionValue.Absolute;

    /// <summary>Per Phase 3 Task 20 cycle 1 — <see langword="true"/> when
    /// the box is <c>position: fixed</c>. Like <c>absolute</c> it's
    /// out-of-flow (<see cref="IsOutOfFlow"/>), but its containing block
    /// is ALWAYS the page / initial containing block (the viewport) and
    /// it repeats on EVERY page. The fixed emission pass keys off this
    /// predicate; the nearest-positioned-ancestor walk (used for
    /// absolute) does not apply.</summary>
    public static bool IsFixedPositioned(this ComputedStyle style)
        => style.ReadPosition() == PositionValue.Fixed;

    /// <summary>Per Phase 3 Task 20 cycle 1 — <see langword="true"/> when
    /// the box is removed from normal flow by positioning:
    /// <c>position: absolute</c> OR <c>position: fixed</c> (CSS
    /// Positioned Layout L3 §6 / §4). Used at the in-flow emission sites
    /// to skip the box from normal flow (it doesn't advance the cursor +
    /// doesn't break margin adjacency); the actual placement happens in
    /// the post-flow absolute / fixed passes. <c>relative</c> and
    /// <c>sticky</c> stay in flow, so they are NOT out-of-flow.</summary>
    public static bool IsOutOfFlow(this ComputedStyle style)
        => style.ReadPosition() is PositionValue.Absolute or PositionValue.Fixed;

    /// <summary>Per Phase 3 Task 19 cycle 1 — does this box establish a
    /// containing block for absolutely-positioned descendants? Per CSS
    /// Positioned Layout L3 §3.3 any box with <c>position</c> other than
    /// <c>static</c> qualifies (cycle 1: relative / absolute / fixed /
    /// sticky). <c>transform</c> / <c>filter</c> / <c>contain</c>
    /// establishment is deferred (those properties aren't wired yet).</summary>
    public static bool EstablishesAbsoluteContainingBlock(this ComputedStyle style)
        => style.ReadPosition() != PositionValue.Static;

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

    /// <summary>Per Phase 3 — flex/grid gutter for <see cref="PropertyId.ColumnGap"/>
    /// / <see cref="PropertyId.RowGap"/> (CSS Box Alignment L3 §8). Unlike
    /// multicol's <see cref="ReadColumnGap"/> (where <c>normal</c> ≈ 1em), for
    /// FLEX + GRID containers <c>normal</c> (and unset / non-length) computes to 0
    /// (§8.1) — so an explicit length is the gutter and everything else is no
    /// gap. Negative lengths floor at 0.</summary>
    public static double ReadFlexGridGapOrZero(this ComputedStyle style, PropertyId gapProperty)
    {
        var slot = style.Get(gapProperty);
        return slot.Tag == ComputedSlotTag.LengthPx
            ? System.Math.Max(0, slot.AsLengthPx())
            : 0.0;
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
    /// scope).
    ///
    /// <para>Per Phase 3 Task 15 L2 post-PR-#62 review hardening F#1 —
    /// the <c>safe</c> / <c>unsafe</c> overflow-position modifiers are
    /// now decoded into the <see cref="ResolvedJustifyContent.Mode"/>
    /// channel instead of being collapsed to <see cref="JustifyContentValue.FlexStart"/>.
    /// Per CSS Box Alignment L3 §5.3, <c>unsafe X</c> HONORS the
    /// specified alignment even on overflow; <c>safe X</c> ONLY changes
    /// behavior on overflow (falling back to safe-start). Pre-fix all
    /// 14 compound keywords (indices 12-25) decoded to
    /// <see cref="JustifyContentValue.FlexStart"/>, so authoring
    /// <c>safe center</c> rendered identically to <c>flex-start</c>.
    /// The two-field return (<see cref="ResolvedJustifyContent.Value"/>
    /// for the base alignment, <see cref="ResolvedJustifyContent.Mode"/>
    /// for the overflow modifier) lets
    /// <c>FlexLayouter.ComputeJustifyContentOffsets</c> apply the
    /// spec-correct overflow fallback per §5.3.</para>
    ///
    /// <para><b>Keyword index mapping.</b> The source-gen'd
    /// <c>BuildJustifyContentTable</c> in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// emits indices in this order: 0=normal, 1=space-between,
    /// 2=space-around, 3=space-evenly, 4=stretch, 5=center, 6=start,
    /// 7=end, 8=flex-start, 9=flex-end, 10=left, 11=right, 12-18=safe
    /// {center, start, end, flex-start, flex-end, left, right},
    /// 19-25=unsafe {…same 7…}. The "safe X" + "unsafe X" compounds
    /// follow the same 7-entry <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// <c>ContentPositions</c> ordering.</para>
    ///
    /// <para><b>Spec mapping notes.</b> <c>normal</c> resolves to
    /// <c>flex-start</c> per CSS Flexbox L1 §8.2 (the flex container's
    /// computed default). <c>stretch</c> is the grid default; for flex
    /// containers it has no effect on main-axis packing per spec, so
    /// L2 maps it to <c>flex-start</c>. The logical aliases
    /// <c>start</c> / <c>end</c> + the directional aliases <c>left</c> /
    /// <c>right</c> map to <c>flex-start</c> / <c>flex-end</c> under
    /// the L1 default LTR + <c>flex-direction: row</c>.</para></summary>
    public static ResolvedJustifyContent ReadJustifyContent(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.JustifyContent, defaultIndex: 0);
        // Decode base position + overflow mode. Indices 12-18 are
        // safe-X; 19-25 are unsafe-X. The "X" follows ContentPositions
        // order (KeywordResolver.cs:121):
        // { center, start, end, flex-start, flex-end, left, right }.
        var (value, mode) = keyword switch
        {
            // Bare values (no overflow modifier).
            0 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Default),  // normal
            1 => (JustifyContentValue.SpaceBetween, OverflowAlignmentMode.Default),
            2 => (JustifyContentValue.SpaceAround, OverflowAlignmentMode.Default),
            3 => (JustifyContentValue.SpaceEvenly, OverflowAlignmentMode.Default),
            4 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Default),  // stretch → flex-start for flex
            5 => (JustifyContentValue.Center, OverflowAlignmentMode.Default),
            6 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Default),  // start → flex-start (LTR row)
            7 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Default),    // end → flex-end (LTR row)
            8 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Default),
            9 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Default),
            10 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Default), // left (LTR row)
            11 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Default),   // right (LTR row)
            // safe <position> — indices 12..18 (center/start/end/flex-start/flex-end/left/right)
            12 => (JustifyContentValue.Center, OverflowAlignmentMode.Safe),
            13 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Safe),    // safe start
            14 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Safe),      // safe end
            15 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Safe),    // safe flex-start
            16 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Safe),      // safe flex-end
            17 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Safe),    // safe left (LTR row)
            18 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Safe),      // safe right (LTR row)
            // unsafe <position> — indices 19..25
            19 => (JustifyContentValue.Center, OverflowAlignmentMode.Unsafe),
            20 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Unsafe),  // unsafe start
            21 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Unsafe),    // unsafe end
            22 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Unsafe),  // unsafe flex-start
            23 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Unsafe),    // unsafe flex-end
            24 => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Unsafe),  // unsafe left (LTR row)
            25 => (JustifyContentValue.FlexEnd, OverflowAlignmentMode.Unsafe),    // unsafe right (LTR row)
            _ => (JustifyContentValue.FlexStart, OverflowAlignmentMode.Default),  // unknown → safe default
        };
        return new ResolvedJustifyContent(value, mode);
    }

    /// <summary>Per Phase 3 Task 15 L4 + L5 — decode
    /// <see cref="PropertyId.FlexDirection"/> per CSS Flexbox L1 §5.1.
    /// L4 ships the two axis-direction values: <c>row</c> (the default;
    /// main axis = inline axis) and <c>column</c> (main axis = block
    /// axis). L5 ships the reversed variants <c>row-reverse</c> +
    /// <c>column-reverse</c>, which per §5.1 "swap main-start and
    /// main-end" along the same row / column axis — items are emitted
    /// at their natural per-item placements but the main-axis ORIGIN
    /// flips (see <see cref="FlexDirectionValueExtensions.IsFlexReverseDirection"/>
    /// for the offset-flip transform applied at the FlexLayouter
    /// emission site).
    ///
    /// <para><b>Keyword index mapping.</b> The source-gen'd
    /// <c>BuildFlexDirectionTable</c> in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// emits indices in the <c>properties.json</c> order: 0=row,
    /// 1=row-reverse, 2=column, 3=column-reverse.</para></summary>
    public static FlexDirectionValue ReadFlexDirection(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.FlexDirection, defaultIndex: 0);
        return keyword switch
        {
            1 => FlexDirectionValue.RowReverse,
            2 => FlexDirectionValue.Column,
            3 => FlexDirectionValue.ColumnReverse,
            _ => FlexDirectionValue.Row,
        };
    }

    /// <summary>Per Phase 3 Task 15 L6 — decode
    /// <see cref="PropertyId.FlexWrap"/> per CSS Flexbox L1 §6.3. L6
    /// ships <c>nowrap</c> (the L1-L5 default) and <c>wrap</c> (multi-
    /// line packing along the cross axis). <c>wrap-reverse</c> decodes
    /// to <see cref="FlexWrapValue.WrapReverse"/> but for L6 it is
    /// treated identically to <see cref="FlexWrapValue.Wrap"/> at the
    /// layouter — the cross-axis line-stacking reversal is L7+ scope;
    /// see <c>docs/deferrals.md#flex-layouter-features</c>.
    ///
    /// <para><b>Keyword index mapping.</b> The source-gen'd
    /// <c>BuildFlexWrapTable</c> in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// emits indices in the <c>properties.json</c> order: 0=nowrap,
    /// 1=wrap, 2=wrap-reverse.</para></summary>
    public static FlexWrapValue ReadFlexWrap(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.FlexWrap, defaultIndex: 0);
        return keyword switch
        {
            1 => FlexWrapValue.Wrap,
            2 => FlexWrapValue.WrapReverse,
            _ => FlexWrapValue.NoWrap,
        };
    }

    /// <summary>Per Phase 3 Task 15 L3 — decode
    /// <see cref="PropertyId.AlignItems"/> per CSS Box Alignment L3 §6 +
    /// CSS Flexbox L1 §8.3. L3 ships the four commonly-used position
    /// values (<c>flex-start</c> / <c>flex-end</c> / <c>center</c> /
    /// <c>stretch</c>) plus the logical-axis aliases (<c>start</c> /
    /// <c>end</c> / <c>self-start</c> / <c>self-end</c>) that map to
    /// <c>flex-start</c> / <c>flex-end</c> under the L1 default LTR +
    /// <c>flex-direction: row</c>; writing-mode-aware mapping is L4+
    /// scope.
    ///
    /// <para><b>Spec defaults.</b> Per CSS Flexbox L1 §8.3 the
    /// <c>align-items</c> property's computed value for a flex container
    /// is <c>stretch</c> when the cascaded value is <c>normal</c> (= the
    /// initial value). This decoder therefore maps <c>normal</c> →
    /// <see cref="AlignItemsValue.Stretch"/>.</para>
    ///
    /// <para><b>L10+ deferrals.</b> Two value families fall through to
    /// <see cref="AlignItemsValue.Stretch"/> (the safe default) in L3:
    /// (a) <c>baseline</c> / <c>first baseline</c> / <c>last baseline</c>
    /// — requires text-shaping integration to align item baselines;
    /// (b) <c>anchor-center</c> — CSS Anchor Positioning, out of scope
    /// for Flexbox L1. The <c>align-self</c> per-item override SHIPPED
    /// in Phase 3 Task 15 L9 (see <see cref="ReadAlignSelf"/>). See
    /// <c>docs/deferrals.md#flex-layouter-features</c> for the L10+
    /// pickup criteria for the remaining items.</para>
    ///
    /// <para><b>Overflow mode.</b> Mirrors L2's two-channel pattern for
    /// <c>justify-content</c>: the bare <c>safe X</c> / <c>unsafe X</c>
    /// compounds (indices 13-26) decode into the
    /// <see cref="OverflowAlignmentMode"/> channel so
    /// <c>FlexLayouter.ComputeAlignItemsPlacement</c> can apply spec-
    /// correct overflow handling per CSS Box Alignment L3 §5.3.</para>
    ///
    /// <para><b>Keyword index mapping.</b> The source-gen'd
    /// <c>BuildAlignItemsTable</c> in
    /// <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// emits indices in this order (VERIFIED against KeywordResolver.cs:286-298
    /// + the SelfPositions array at KeywordResolver.cs:114-115 which
    /// orders the seven <c>&lt;self-position&gt;</c> values as
    /// <c>center, start, end, self-start, self-end, flex-start,
    /// flex-end</c>): 0=normal, 1=stretch, 2=anchor-center, 3=baseline,
    /// 4=first baseline, 5=last baseline, 6=center, 7=start, 8=end,
    /// 9=self-start, 10=self-end, 11=flex-start, 12=flex-end, 13-19=safe
    /// {center, start, end, self-start, self-end, flex-start, flex-end},
    /// 20-26=unsafe {…same 7…}.</para></summary>
    public static ResolvedAlignItems ReadAlignItems(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.AlignItems, defaultIndex: 0);
        var (value, mode) = DecodeSelfPositionGrid(keyword);
        return new ResolvedAlignItems(value, mode);
    }

    /// <summary>Per Phase 3 Task 15 L9 post-PR-#69 architecture
    /// recommendation — shared decoder for the 27-entry
    /// &lt;self-position&gt; + safe/unsafe grid consumed by BOTH
    /// <see cref="ReadAlignItems"/> AND <see cref="ReadAlignSelf"/>
    /// (the latter shifts its keyword by -1 to skip the leading
    /// <c>auto</c> entry per <c>BuildAlignSelfTable</c>). Returning a
    /// tuple keeps the helper agnostic to which resolved-* record
    /// wraps the (value, mode) pair.
    ///
    /// <para><b>Grid layout</b> (matches the SelfPositions ordering in
    /// <c>KeywordResolver</c>): 0=normal → Stretch (per CSS Flexbox §8.3),
    /// 1=stretch, 2=anchor-center → Stretch (L10+ scope), 3-5=baseline
    /// triple → Stretch (L10+ scope), 6-12=&lt;self-position&gt; (center,
    /// start, end, self-start, self-end, flex-start, flex-end), 13-19=
    /// safe + 7 self-positions, 20-26=unsafe + 7 self-positions. Total
    /// = 27 entries.</para>
    ///
    /// <para>Pre-PR-#69 the two readers held duplicate switch tables
    /// with align-self shifted by +1 — the architecture-rec hardening
    /// collapses them into a single source of truth so writing-mode
    /// support, anchor-center, and proper baseline alignment (all
    /// L10+) land in one site.</para>
    /// </summary>
    private static (AlignItemsValue Value, OverflowAlignmentMode Mode)
        DecodeSelfPositionGrid(int keyword) => keyword switch
    {
        // Bare values (no overflow modifier).
        0 => (AlignItemsValue.Stretch, OverflowAlignmentMode.Default),     // normal → stretch (CSS Flexbox §8.3)
        1 => (AlignItemsValue.Stretch, OverflowAlignmentMode.Default),     // stretch
        2 => (AlignItemsValue.Stretch, OverflowAlignmentMode.Default),     // anchor-center → stretch (L10+ scope)
        3 => (AlignItemsValue.Stretch, OverflowAlignmentMode.Default),     // baseline → stretch (L10+ scope)
        4 => (AlignItemsValue.Stretch, OverflowAlignmentMode.Default),     // first baseline → stretch (L10+ scope)
        5 => (AlignItemsValue.Stretch, OverflowAlignmentMode.Default),     // last baseline → stretch (L10+ scope)
        6 => (AlignItemsValue.Center, OverflowAlignmentMode.Default),
        7 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Default),   // start → flex-start (LTR row)
        8 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Default),     // end → flex-end (LTR row)
        9 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Default),   // self-start → flex-start (LTR row)
        10 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Default),    // self-end → flex-end (LTR row)
        11 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Default),
        12 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Default),
        // safe <SelfPositions[0..6]> — indices 13..19.
        13 => (AlignItemsValue.Center, OverflowAlignmentMode.Safe),
        14 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Safe),     // safe start
        15 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Safe),       // safe end
        16 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Safe),     // safe self-start
        17 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Safe),       // safe self-end
        18 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Safe),     // safe flex-start
        19 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Safe),       // safe flex-end
        // unsafe <SelfPositions[0..6]> — indices 20..26.
        20 => (AlignItemsValue.Center, OverflowAlignmentMode.Unsafe),
        21 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Unsafe),   // unsafe start
        22 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Unsafe),     // unsafe end
        23 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Unsafe),   // unsafe self-start
        24 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Unsafe),     // unsafe self-end
        25 => (AlignItemsValue.FlexStart, OverflowAlignmentMode.Unsafe),   // unsafe flex-start
        26 => (AlignItemsValue.FlexEnd, OverflowAlignmentMode.Unsafe),     // unsafe flex-end
        _ => (AlignItemsValue.Stretch, OverflowAlignmentMode.Default),     // unknown → safe default
    };

    /// <summary>Per Phase 3 Task 15 L9 — decode
    /// <see cref="PropertyId.AlignSelf"/> per CSS Box Alignment L3 §4.3.
    /// Mirrors <see cref="ReadAlignItems"/>'s decoding (= the same
    /// &lt;self-position&gt; family + the same safe/unsafe overflow
    /// modes) but ADDS the <c>auto</c> keyword at index 0 — which
    /// signals "defer to the container's <c>align-items</c>" per CSS
    /// Box Alignment §4.3 ("If the value of align-self is auto, its
    /// used value is the value of align-items on the parent").
    ///
    /// <para><b>Spec defaults.</b> The cascaded default per
    /// properties.json is <c>auto</c>, so any item that doesn't
    /// declare align-self gets the container's align-items behavior —
    /// the L3 behavior is preserved verbatim.</para>
    ///
    /// <para><b>Keyword index mapping (verified against
    /// <c>KeywordResolver.BuildAlignSelfTable</c>):</b> 0=auto,
    /// 1=normal, 2=stretch, 3=anchor-center, 4=baseline, 5=first baseline,
    /// 6=last baseline, 7-13=&lt;self-position&gt; (center, start, end,
    /// self-start, self-end, flex-start, flex-end), 14-20=safe X,
    /// 21-27=unsafe X. Total = 28 entries. Indices 1-13 mirror
    /// align-items indices 0-12 shifted by +1; indices 14-27 mirror
    /// align-items indices 13-26 shifted by +1. Baseline / anchor-center
    /// approximate to Stretch in L9 (mirrors ReadAlignItems); proper
    /// baseline alignment is L10+ scope (text-shaping integration).</para>
    /// </summary>
    public static ResolvedAlignSelf ReadAlignSelf(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.AlignSelf, defaultIndex: 0);
        // Per Phase 3 Task 15 L9 post-PR-#69 architecture rec — the
        // shared <self-position> grid (DecodeSelfPositionGrid) is the
        // single source of truth for the 27-entry grid that
        // align-items + align-self both consume. align-self's table
        // adds `auto` at index 0, so we shift the keyword by -1 before
        // delegating; the special-case (index 0 = auto) is handled
        // separately. Unknown indices (>=29 or negative) fall through
        // to auto (the safe default per CSS Box Alignment §4.3).
        if (keyword == 0) return new ResolvedAlignSelf(AlignSelfValue.Auto, OverflowAlignmentMode.Default);
        var shifted = keyword - 1;
        if (shifted < 0 || shifted > 26)
        {
            return new ResolvedAlignSelf(AlignSelfValue.Auto, OverflowAlignmentMode.Default);
        }
        var (value, mode) = DecodeSelfPositionGrid(shifted);
        return new ResolvedAlignSelf(AlignItemsValueToAlignSelfValue(value), mode);
    }

    /// <summary>Per Phase 3 Task 15 L9 post-PR-#69 architecture rec —
    /// 1:1 mapping from <see cref="AlignItemsValue"/> (returned by the
    /// shared <see cref="DecodeSelfPositionGrid"/>) to the
    /// <see cref="AlignSelfValue"/> enum. The enums are parallel
    /// (Stretch / FlexStart / FlexEnd / Center) but stored in separate
    /// declarations because <see cref="AlignSelfValue"/> additionally
    /// admits <see cref="AlignSelfValue.Auto"/> (= defer to the
    /// container's align-items). This mapping never sees Auto — that
    /// case is handled at the call site before delegating to the
    /// shared decoder.</summary>
    private static AlignSelfValue AlignItemsValueToAlignSelfValue(AlignItemsValue value) =>
        value switch
        {
            AlignItemsValue.Stretch => AlignSelfValue.Stretch,
            AlignItemsValue.FlexStart => AlignSelfValue.FlexStart,
            AlignItemsValue.FlexEnd => AlignSelfValue.FlexEnd,
            AlignItemsValue.Center => AlignSelfValue.Center,
            _ => AlignSelfValue.Stretch, // defensive — never reached for known AlignItemsValue values
        };

    /// <summary>Per Phase 3 Task 15 L9 — resolve an item's effective
    /// align-items value by deferring to the container's
    /// <see cref="ResolvedAlignItems"/> when the item's align-self is
    /// <see cref="AlignSelfValue.Auto"/>. Returns the container's
    /// (value, mode) tuple for the Auto case; otherwise converts the
    /// item's align-self enum to the matching align-items enum + uses
    /// the item's own overflow mode.</summary>
    public static ResolvedAlignItems ResolveAgainstContainerAlignItems(
        this ResolvedAlignSelf alignSelf,
        ResolvedAlignItems containerAlignItems)
    {
        return alignSelf.Value switch
        {
            AlignSelfValue.Auto => containerAlignItems,
            AlignSelfValue.Stretch => new ResolvedAlignItems(AlignItemsValue.Stretch, alignSelf.Mode),
            AlignSelfValue.FlexStart => new ResolvedAlignItems(AlignItemsValue.FlexStart, alignSelf.Mode),
            AlignSelfValue.FlexEnd => new ResolvedAlignItems(AlignItemsValue.FlexEnd, alignSelf.Mode),
            AlignSelfValue.Center => new ResolvedAlignItems(AlignItemsValue.Center, alignSelf.Mode),
            _ => containerAlignItems, // defensive — unknown self values fall back to container
        };
    }

    /// <summary>Per Phase 3 Task 15 L7 — decode
    /// <see cref="PropertyId.AlignContent"/> per CSS Box Alignment L3 §6 +
    /// CSS Flexbox L1 §8.4. L7 ships the seven base values plus the
    /// safe/unsafe overflow modes:
    /// <see cref="AlignContentValue.FlexStart"/>,
    /// <see cref="AlignContentValue.FlexEnd"/>,
    /// <see cref="AlignContentValue.Center"/>,
    /// <see cref="AlignContentValue.SpaceBetween"/>,
    /// <see cref="AlignContentValue.SpaceAround"/>,
    /// <see cref="AlignContentValue.SpaceEvenly"/>, and
    /// <see cref="AlignContentValue.Stretch"/> (the computed default for
    /// the initial <c>normal</c> value per CSS Flexbox L1 §8.4).
    ///
    /// <para><b>Spec defaults.</b> Per CSS Flexbox L1 §8.4 the
    /// <c>align-content</c> property's computed value for a flex container
    /// is <c>stretch</c> when the cascaded value is <c>normal</c> (= the
    /// initial value). This decoder maps <c>normal</c> →
    /// <see cref="AlignContentValue.Stretch"/>.</para>
    ///
    /// <para><b>L8+ deferrals.</b> Logical-axis aliases (<c>start</c> /
    /// <c>end</c>) and directional aliases (<c>left</c> / <c>right</c>)
    /// map to <c>flex-start</c> / <c>flex-end</c> under the L1 default
    /// LTR + <c>flex-direction: row</c> — writing-mode-aware mapping is
    /// L8+ scope. <c>safe</c> / <c>unsafe</c> overflow-position modifiers
    /// (compound keywords like <c>safe center</c>) decode into the
    /// <see cref="OverflowAlignmentMode"/> channel of
    /// <see cref="ResolvedAlignContent"/>. Per Phase 3 Task 15 L7
    /// post-PR-#67 hardening F#2, the per-mode overflow semantics per
    /// CSS Box Alignment L3 §5.3 are now implemented in L7 (mirroring
    /// the L2 justify-content pattern): <c>safe X</c> falls back to
    /// safe-start; <c>unsafe X</c> honors the natural (possibly-
    /// negative) offset; default mode gives distribution values + stretch
    /// the safe-start fallback while positional values keep their natural
    /// offset. See <c>FlexLayouter.ComputeAlignContentOffsets</c> for the
    /// branching logic.</para>
    ///
    /// <para><b>Keyword index mapping (Phase 3 Task 15 L7 post-PR-#67 F#6 —
    /// baseline added).</b> The source-gen'd <c>BuildAlignContentTable</c>
    /// in <see cref="NetPdf.Css.ComputedValues.PropertyResolvers.KeywordResolver"/>
    /// emits indices in this order (VERIFIED against KeywordResolver.cs
    /// <c>BuildAlignContentTable</c> + the <c>ContentPositions</c> array
    /// at KeywordResolver.cs:121-122 which orders the seven
    /// <c>&lt;content-position&gt;</c> values as <c>center, start, end,
    /// flex-start, flex-end, left, right</c>): 0=normal,
    /// 1=space-between, 2=space-around, 3=space-evenly, 4=stretch,
    /// 5=baseline, 6=first baseline, 7=last baseline, 8=center, 9=start,
    /// 10=end, 11=flex-start, 12=flex-end, 13=left, 14=right,
    /// 15-21=safe {center, start, end, flex-start, flex-end, left,
    /// right}, 22-28=unsafe {…same 7…}. Total = 29 entries.</para></summary>
    public static ResolvedAlignContent ReadAlignContent(this ComputedStyle style)
    {
        var keyword = style.ReadKeywordOrDefault(PropertyId.AlignContent, defaultIndex: 0);
        return keyword switch
        {
            // 0=normal → Stretch (CSS Flexbox §8.4 spec default).
            0 => new ResolvedAlignContent(AlignContentValue.Stretch, OverflowAlignmentMode.Default),
            // 1-4 = <content-distribution>.
            1 => new ResolvedAlignContent(AlignContentValue.SpaceBetween, OverflowAlignmentMode.Default),
            2 => new ResolvedAlignContent(AlignContentValue.SpaceAround, OverflowAlignmentMode.Default),
            3 => new ResolvedAlignContent(AlignContentValue.SpaceEvenly, OverflowAlignmentMode.Default),
            4 => new ResolvedAlignContent(AlignContentValue.Stretch, OverflowAlignmentMode.Default),
            // 5-7 = <baseline-position> (Phase 3 Task 15 L7 post-PR-#67
            // F#6). L7 approximates as Stretch (the safe default);
            // proper baseline alignment is text-shaping integration
            // scope (L8+). Mirrors how align-items handles the same
            // baseline triple (see ReadAlignItems above).
            5 => new ResolvedAlignContent(AlignContentValue.Stretch, OverflowAlignmentMode.Default),      // baseline → stretch (L8+ scope)
            6 => new ResolvedAlignContent(AlignContentValue.Stretch, OverflowAlignmentMode.Default),      // first baseline → stretch (L8+ scope)
            7 => new ResolvedAlignContent(AlignContentValue.Stretch, OverflowAlignmentMode.Default),      // last baseline → stretch (L8+ scope)
            // 8-14 = <content-position>: center, start, end, flex-start,
            // flex-end, left, right (LTR + horizontal-tb mapping).
            8 => new ResolvedAlignContent(AlignContentValue.Center, OverflowAlignmentMode.Default),
            9 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Default),    // start → flex-start (LTR)
            10 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Default),     // end → flex-end (LTR)
            11 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Default),
            12 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Default),
            13 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Default),   // left → flex-start (LTR)
            14 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Default),     // right → flex-end (LTR)
            // 15-21 = safe <content-position>.
            15 => new ResolvedAlignContent(AlignContentValue.Center, OverflowAlignmentMode.Safe),
            16 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Safe),      // safe start
            17 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Safe),        // safe end
            18 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Safe),      // safe flex-start
            19 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Safe),        // safe flex-end
            20 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Safe),      // safe left (LTR)
            21 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Safe),        // safe right (LTR)
            // 22-28 = unsafe <content-position>.
            22 => new ResolvedAlignContent(AlignContentValue.Center, OverflowAlignmentMode.Unsafe),
            23 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Unsafe),    // unsafe start
            24 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Unsafe),      // unsafe end
            25 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Unsafe),    // unsafe flex-start
            26 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Unsafe),      // unsafe flex-end
            27 => new ResolvedAlignContent(AlignContentValue.FlexStart, OverflowAlignmentMode.Unsafe),    // unsafe left (LTR)
            28 => new ResolvedAlignContent(AlignContentValue.FlexEnd, OverflowAlignmentMode.Unsafe),      // unsafe right (LTR)
            _ => new ResolvedAlignContent(AlignContentValue.Stretch, OverflowAlignmentMode.Default),      // unknown → safe default
        };
    }

    /// <summary>Per Phase 3 Task 15 L8 — read the <c>flex-grow</c>
    /// factor per CSS Flexbox L1 §7.1. The cascade default is <c>0</c>
    /// (= the item never grows). NumberResolver normalizes negative
    /// values to invalid (rejected at parse time per CSS Flexbox §7.1
    /// — negative grow/shrink factors are spec-disallowed), so the
    /// read clamps the returned value at 0 defensively. A non-Number
    /// slot (e.g., invalidated declaration that fell back to initial
    /// value) also returns 0.</summary>
    public static double ReadFlexGrow(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.FlexGrow);
        if (slot.Tag != ComputedSlotTag.Number) return 0.0;
        var n = slot.AsNumber();
        return n > 0.0 ? n : 0.0;
    }

    /// <summary>Per Phase 3 Task 15 L8 — read the <c>flex-shrink</c>
    /// factor per CSS Flexbox L1 §7.1. The cascade default is <c>1</c>
    /// (= the item shrinks at the unit rate when free-space is
    /// negative). NumberResolver normalizes negatives to invalid;
    /// non-Number slots fall back to the spec default of 1 (NOT 0 —
    /// flex-shrink's initial value is 1 per §7.1, which is the
    /// foundation of "items shrink to fit"). The cascade-supplied
    /// default <c>1</c> from properties.json should make this branch
    /// unreachable in practice, but the explicit fallback documents
    /// the spec invariant.</summary>
    public static double ReadFlexShrink(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.FlexShrink);
        if (slot.Tag != ComputedSlotTag.Number) return 1.0;
        var n = slot.AsNumber();
        return n > 0.0 ? n : 0.0;
    }

    /// <summary>Per Phase 3 Task 15 L8 — read the <c>flex-basis</c>
    /// value per CSS Flexbox L1 §7.2. Decodes into a
    /// <see cref="ResolvedFlexBasis"/>:
    /// <list type="bullet">
    ///   <item><c>auto</c> (KeywordIdAuto = 0) → delegate to the item's
    ///   declared main-size (<c>width</c> for row, <c>height</c> for
    ///   column).</item>
    ///   <item><c>content</c> (KeywordIdContent = 1) → use the item's
    ///   intrinsic content size. L8 approximates this as
    ///   <see cref="FlexBasisKind.Auto"/> (= delegate to declared
    ///   main-size) until intrinsic sizing lands; the Content variant
    ///   is preserved on the resolved struct for future use.</item>
    ///   <item><c>&lt;length&gt;</c> (LengthPx slot) → use the explicit
    ///   pixel value as the hypothetical main-size.</item>
    ///   <item><c>&lt;percentage&gt;</c> (Percentage slot) → resolve
    ///   against the container's main-size; per §9.2.3 a percentage
    ///   flex-basis is treated as <c>auto</c> if the container's main
    ///   size is indefinite. L8 supports definite-main-size containers
    ///   only.</item>
    ///   <item>Any unset / invalid slot → Auto (spec default per
    ///   §7.2).</item>
    /// </list></summary>
    public static ResolvedFlexBasis ReadFlexBasis(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.FlexBasis);
        return slot.Tag switch
        {
            ComputedSlotTag.Keyword => slot.AsKeyword() switch
            {
                0 => new ResolvedFlexBasis(FlexBasisKind.Auto, 0.0),       // auto
                1 => new ResolvedFlexBasis(FlexBasisKind.Content, 0.0),    // content
                _ => new ResolvedFlexBasis(FlexBasisKind.Auto, 0.0),       // unknown → auto
            },
            ComputedSlotTag.LengthPx => new ResolvedFlexBasis(FlexBasisKind.LengthPx, slot.AsLengthPx()),
            ComputedSlotTag.Percentage => new ResolvedFlexBasis(FlexBasisKind.Percentage, slot.AsPercentage()),
            _ => new ResolvedFlexBasis(FlexBasisKind.Auto, 0.0),
        };
    }

    /// <summary>Per Phase 3 Task 15 L10 — read the <c>order</c>
    /// property per CSS Flexbox L1 §5.4 ("order Property"). Items
    /// with a lower order value visually pack earlier on the main
    /// axis; items with equal order preserve DOM order (= stable
    /// sort). Default is <c>0</c> (= source order). Negative values
    /// are allowed and produce items that pack BEFORE the default-0
    /// items.
    ///
    /// <para><b>Layout impact.</b> The FlexLayouter pre-sorts its
    /// block-level children by (order, DOM-index) before line packing
    /// + per-line emission. The BlockLayouter's
    /// <c>PreMeasureFlexMultiLineCrossExtent</c> pre-measure performs
    /// the same sort so pre-measure parity with the layout pass is
    /// preserved (= the L8 F#1 hardening shared-sizing pattern).</para>
    ///
    /// <para><b>Non-Integer slot fallback.</b> Returns 0 when the
    /// cascaded slot is not Integer (e.g., unset / invalid /
    /// unsupported). NumberResolver's Integer path accepts negative
    /// signed integers — <c>order</c> is one of the few non-negative-
    /// gated CSS Integer properties (per CSS Flexbox §5.4 negatives
    /// are spec-valid).</para></summary>
    public static int ReadOrder(this ComputedStyle style)
    {
        var slot = style.Get(PropertyId.Order);
        return slot.Tag == ComputedSlotTag.Integer ? slot.AsInteger() : 0;
    }

    /// <summary>Per Phase 3 Task 15 L10 — return the child-index
    /// sequence of <paramref name="flexContainer"/>'s block-level
    /// children in their EFFECTIVE FLEX ORDER per CSS Flexbox L1 §5.4
    /// — sorted by (order ascending, DOM-index ascending). Items with
    /// equal order preserve DOM order (stable-sort guarantee). The
    /// returned list contains only block-level children; non-block-
    /// level children (e.g., whitespace TextRuns) are excluded so the
    /// flex algorithm doesn't conflate them with items.
    ///
    /// <para><b>Why a shared helper.</b> Per the L8 F#1 hardening
    /// precedent, the FlexLayouter's <c>PackLines</c> +
    /// <c>ResolveFlexibleMainSizes</c> + the BlockLayouter's
    /// <c>PreMeasureFlexMultiLineCrossExtent</c> all must walk the
    /// SAME effective-order sequence. Centralizing the read here
    /// guarantees pre-measure parity with the layout pass — if the
    /// sort drifted between sites, the wrapper's cross-extent
    /// estimate would diverge from the actual emission.</para>
    ///
    /// <para><b>Stability.</b> .NET's <see cref="System.Collections.Generic.List{T}"/>.Sort
    /// is unstable; we use a two-key comparer (order, DOM-index) so
    /// the secondary key emulates stability.</para>
    ///
    /// <para><b>Per Phase 3 Task 15 L10 post-PR-#70 performance
    /// hardening</b> — the comparator no longer calls
    /// <see cref="ReadOrder"/> on every comparison
    /// (= O(n log n) cascade slot lookups for non-trivial flex
    /// containers). Pre-fix, a large flex container with N items
    /// performed ~N log N <c>style.Get(PropertyId.Order)</c> calls
    /// during sort; post-fix, the helper allocates a temporary
    /// <c>(domIndex, order)</c> tuple list, reads each order once
    /// (= O(N) cascade lookups), sorts the tuple list by order
    /// (using a comparator that only touches the cached int field +
    /// the int DOM index), then projects to a plain index list.</para>
    ///
    /// <para><b>Cancellation</b> (post-PR-#70 hardening): the helper
    /// observes the supplied cancellation token at the start + per
    /// item during the initial scan. Sorting a fixed-size list is
    /// fast enough that mid-sort cancellation isn't needed.</para>
    ///
    /// <para><b>Allocation note.</b> Returns a fresh List every call;
    /// callers may convert to a Span if hot. L10 keeps the List shape
    /// to mirror the existing layouter style (PackLines, etc.); a
    /// pooling refactor is L11+ scope.</para></summary>
    /// <param name="flexContainer">The flex container box whose
    /// children should be sorted.</param>
    /// <param name="cancellationToken">Propagates cancellation
    /// through the initial child scan. Optional; defaults to
    /// <see cref="System.Threading.CancellationToken.None"/> so callers
    /// that don't have a token in scope can still use the helper.</param>
    /// <returns>A list of child indices (into <c>flexContainer.Children</c>)
    /// in effective flex order. Non-block-level children are
    /// excluded.</returns>
    public static System.Collections.Generic.List<int>
        GetFlexChildrenInOrderSequence(
            this Boxes.Box flexContainer,
            System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Per Phase 3 Task 15 L10 post-PR-#70 performance hardening —
        // read each item's order ONCE during the initial filter pass
        // + carry the (domIndex, order) pair in a small struct that
        // the comparator dereferences without touching the cascade.
        // For containers with up to a few dozen items the saving is
        // ~N cascade lookups (= the difference between O(N) and
        // O(N log N)).
        var indexedOrders = new System.Collections.Generic.List<(int DomIndex, int Order)>();
        var hasNonZeroOrder = false;
        for (var i = 0; i < flexContainer.Children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!flexContainer.Children[i].IsBlockLevel) continue;
            // Per Phase 3 Task 19 cycle 2a post-PR-#113 review P1#2 (+
            // Task 20 cycle 1 post-PR-#115 review P1) — an OUT-OF-FLOW
            // child of a flex container (`position: absolute` OR
            // `position: fixed`) is NOT a flex item (CSS Flexbox L1 §4 /
            // CSS Positioned Layout L3 §3): it's out-of-flow + doesn't
            // participate in flex line packing. Excluding it keeps it
            // from displacing/sizing real flex items + from being emitted
            // by the FlexLayouter; the establishing BlockLayouter's
            // post-flow abspos / fixed pass owns its placement (otherwise
            // double emission).
            if (flexContainer.Children[i].Style.IsOutOfFlow()) continue;
            var order = flexContainer.Children[i].Style.ReadOrder();
            if (order != 0) hasNonZeroOrder = true;
            indexedOrders.Add((i, order));
        }

        // Fast-path: every item has order 0 → DOM order IS effective
        // order. Project the DOM indices directly without sorting.
        if (!hasNonZeroOrder)
        {
            var domOrder = new System.Collections.Generic.List<int>(indexedOrders.Count);
            foreach (var t in indexedOrders) domOrder.Add(t.DomIndex);
            return domOrder;
        }

        // Two-key stable sort: primary (cached order asc), secondary
        // (DOM index asc) emulates stability. The comparator only
        // reads two ints per comparison — no cascade lookups.
        indexedOrders.Sort((a, b) =>
        {
            if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
            return a.DomIndex.CompareTo(b.DomIndex);
        });

        // Project (DomIndex, Order) tuples → DomIndex.
        var sorted = new System.Collections.Generic.List<int>(indexedOrders.Count);
        foreach (var t in indexedOrders) sorted.Add(t.DomIndex);
        return sorted;
    }

    /// <summary>Per Phase 3 Task 15 L8 + post-PR-#68 hardening F#1 —
    /// compute a flex item's hypothetical main-size per CSS Flexbox L1
    /// §9.2. Shared between <c>FlexLayouter.PackLines</c> +
    /// <c>FlexLayouter.ResolveFlexibleMainSizes</c> and the
    /// <c>BlockLayouter.PreMeasureFlexMultiLineCrossExtent</c> so
    /// line-collection uses the same flex-basis-aware size in both
    /// passes (= line-boundary parity per §9.3).
    ///
    /// <para><b>Resolution table:</b> <see cref="FlexBasisKind.LengthPx"/>
    /// returns the explicit pixel value (floored at 0);
    /// <see cref="FlexBasisKind.Percentage"/> resolves against
    /// <paramref name="containerMainSize"/> (definite 0 yields 0; non-
    /// finite container size falls back to the declared property);
    /// <see cref="FlexBasisKind.Auto"/> / <see cref="FlexBasisKind.Content"/>
    /// delegate to the item's declared main-size property
    /// (Content is approximated as Auto in L8 — intrinsic sizing is
    /// L10+ scope).</para></summary>
    /// <param name="item">The flex item box.</param>
    /// <param name="mainSizeProperty">The direction-resolved main-size
    /// property (<see cref="PropertyId.Width"/> for row,
    /// <see cref="PropertyId.Height"/> for column).</param>
    /// <param name="containerMainSize">The container's main-axis
    /// content extent — used to resolve percentage flex-basis values.</param>
    public static double ResolveFlexItemHypotheticalMainSize(
        this Boxes.Box item,
        PropertyId mainSizeProperty,
        double containerMainSize)
    {
        // Flex box-sizing cycle — the hypothetical (= flex base) main size is returned as a
        // BORDER box honoring `box-sizing` (CSS Basic UI 4 §10, via the shared
        // BoxSizingHelper): for `content-box` (initial) a definite flex-basis / declared
        // size is the CONTENT box, so the border box adds the item's main-axis border +
        // padding; for `border-box` the declared size IS the border box. The chrome is
        // derived from the direction-resolved main-size property (Width → inline, Height →
        // block). A DEFINITE size (a flex-basis length / percentage, or a declared LengthPx —
        // INCLUDING an explicit 0) maps through BoxSizingHelper, so a 0-size box with chrome
        // floors at its chrome (post-PR-#190 Copilot review — a definite 0 must not drop
        // chrome). Only an AUTO / content-determined size (or an unresolved percentage declared
        // size) stays 0 — content sizing grows it later + adds the chrome there. This is what
        // makes a flex item's emitted border box account for its own border/padding.
        var mainChrome = item.Style.AxisBorderPaddingPx(mainSizeProperty);
        var basis = item.Style.ReadFlexBasis();
        switch (basis.Kind)
        {
            case FlexBasisKind.LengthPx:
                // A definite length (incl. 0) → border box (0 → chrome). flex-basis is
                // non-negative (rejected at cascade); Math.Max is defensive.
                return BoxSizingHelper.DeclaredToBorderBox(item.Style, Math.Max(0, basis.Value), mainChrome);
            case FlexBasisKind.Percentage:
                if (!double.IsFinite(containerMainSize))
                {
                    // Non-finite container size — the only defensive
                    // fallback. A definite 0 IS valid per CSS Values L4 §6.5;
                    // only NaN / ±Infinity indicates a contract violation.
                    return ResolveDeclaredMainBorderBox(item, mainSizeProperty, mainChrome);
                }
                var pct = Math.Max(0, basis.Value / 100.0 * containerMainSize);
                return BoxSizingHelper.DeclaredToBorderBox(item.Style, pct, mainChrome);
            case FlexBasisKind.Auto:
            case FlexBasisKind.Content:
            default:
                return ResolveDeclaredMainBorderBox(item, mainSizeProperty, mainChrome);
        }
    }

    /// <summary>Flex box-sizing cycle — the item's DECLARED main size mapped to a BORDER box
    /// (via <see cref="BoxSizingHelper.DeclaredToBorderBox"/>) when DEFINITE (a <c>LengthPx</c>
    /// slot, INCLUDING an explicit 0 — which floors at the chrome), else 0 (auto / unresolved
    /// percentage / content-determined; the chrome is added by content sizing). Gating on the
    /// slot TAG (not <c>value &gt; 0</c>) keeps an explicit <c>0</c> definite — post-PR-#190
    /// Copilot review. Shared by the auto/content flex-basis branch + the non-finite-container
    /// fallback above.</summary>
    private static double ResolveDeclaredMainBorderBox(
        Boxes.Box item, PropertyId mainSizeProperty, double mainChrome)
    {
        var slot = item.Style.Get(mainSizeProperty);
        return slot.Tag == ComputedSlotTag.LengthPx
            ? BoxSizingHelper.DeclaredToBorderBox(item.Style, Math.Max(0, slot.AsLengthPx()), mainChrome)
            : 0;
    }

    /// <summary>Per Phase 3 Task 15 L12 — read the item's
    /// (min, max) main-axis size for the §9.7 step-4 clamping
    /// iteration. Returns <c>(min, max)</c> as doubles:
    /// <list type="bullet">
    ///   <item><c>min</c> = the resolved <c>min-width</c> (row) or
    ///   <c>min-height</c> (column) length in pixels; 0 when the
    ///   slot is auto / unset / non-LengthPx (= the L12 floor;
    ///   spec-correct `min-width: auto` for flex items resolves to
    ///   the item's intrinsic content size per CSS Sizing §5.5,
    ///   which is L13+ scope pending intrinsic-sizing integration).</item>
    ///   <item><c>max</c> = the resolved <c>max-width</c> /
    ///   <c>max-height</c> length in pixels; <see cref="double.PositiveInfinity"/>
    ///   when the slot is none / unset / non-LengthPx (= no upper
    ///   bound). Percentages are L13+ scope (need container main-size
    ///   resolution at the per-item site).</item>
    /// </list>
    ///
    /// <para><b>Used by</b> <c>FlexLayouter.ResolveFlexibleMainSizes</c>
    /// per CSS Flexbox L1 §9.7 step 4: after the initial grow/shrink
    /// distribution, items are clamped to <c>[min, max]</c> + the
    /// clamped-off space is redistributed among non-frozen items in
    /// the next iteration.</para></summary>
    public static (double Min, double Max) ResolveFlexItemMinMaxMainSize(
        this Boxes.Box item,
        PropertyId minSizeProperty,
        PropertyId maxSizeProperty)
    {
        // Flex box-sizing cycle — an EXPLICIT min/max length is mapped to a BORDER box
        // (honoring `box-sizing`, same chrome as the hypothetical) so it clamps the
        // border-box-resolved main size on the same axis. An unset min (→ 0 floor) / max
        // (→ no bound) keeps its prior value (the `auto` min-width intrinsic resolution is
        // still L13+ scope; percentages still L13+).
        var chrome = item.Style.AxisBorderPaddingPx(minSizeProperty);
        var minSlot = item.Style.Get(minSizeProperty);
        var maxSlot = item.Style.Get(maxSizeProperty);
        var min = minSlot.Tag == ComputedSlotTag.LengthPx
            ? BoxSizingHelper.DeclaredToBorderBox(item.Style, Math.Max(0, minSlot.AsLengthPx()), chrome)
            : 0.0;
        var max = maxSlot.Tag == ComputedSlotTag.LengthPx
            ? BoxSizingHelper.DeclaredToBorderBox(item.Style, Math.Max(0, maxSlot.AsLengthPx()), chrome)
            : double.PositiveInfinity;
        return (min, max);
    }

    /// <summary>CSS 2.2 §10.4 (width) / §10.7 (height) — clamp a resolved BORDER-box
    /// size by the box's <c>min-*</c> / <c>max-*</c> on the SAME axis (chosen by
    /// <paramref name="minProperty"/>). An explicit min/max <c>LengthPx</c> maps to
    /// the border box via the same chrome (honoring <c>box-sizing</c>, mirroring
    /// <see cref="ResolveFlexItemMinMaxMainSize"/>); <c>max</c> is applied first then
    /// <c>min</c>, so min wins when min &gt; max (§10.4). <c>min: auto</c> (unset) →
    /// 0 floor (a no-op past the chrome); <c>max: none</c> (unset) → no upper bound.
    ///
    /// <para>A PERCENTAGE min/max resolves against <paramref name="containingSize"/>
    /// (CSS 2.1 §10.4/§10.7). When the containing size is INDEFINITE — the default
    /// <see cref="double.NaN"/>, used by the inline auto-fill / float fast paths and
    /// the indefinite block axis — the §10.7 indefinite rule applies: a <c>%</c> max
    /// computes to <c>none</c> (no upper bound) and a <c>%</c> min to <c>0</c> (no
    /// floor), so percentages are skipped. Callers that don't pass a definite
    /// containing size stay byte-identical (a <c>%</c> min/max remains a no-op for
    /// them, exactly as before). When neither min nor max is set this is the IDENTITY,
    /// so non-min/max block layout is unchanged.</para></summary>
    public static double ClampBorderBoxToMinMax(
        this Boxes.Box box, double borderBoxSize,
        PropertyId minProperty, PropertyId maxProperty,
        double containingSize = double.NaN)
    {
        var definiteContaining = double.IsFinite(containingSize) && containingSize >= 0;
        var chrome = box.Style.AxisBorderPaddingPx(minProperty);

        var maxSlot = box.Style.Get(maxProperty);
        if (maxSlot.Tag == ComputedSlotTag.LengthPx)
        {
            var maxBorderBox = BoxSizingHelper.DeclaredToBorderBox(
                box.Style, Math.Max(0, maxSlot.AsLengthPx()), chrome);
            borderBoxSize = Math.Min(borderBoxSize, maxBorderBox);
        }
        else if (maxSlot.Tag == ComputedSlotTag.Percentage && definiteContaining)
        {
            // §10.7 — a definite `%` max-* resolves against the containing size; an
            // indefinite one computes to `none` (handled by the definiteContaining gate).
            var maxBorderBox = BoxSizingHelper.DeclaredToBorderBox(
                box.Style, Math.Max(0, maxSlot.AsPercentage() / 100.0 * containingSize), chrome);
            borderBoxSize = Math.Min(borderBoxSize, maxBorderBox);
        }

        var minSlot = box.Style.Get(minProperty);
        if (minSlot.Tag == ComputedSlotTag.LengthPx)
        {
            var minBorderBox = BoxSizingHelper.DeclaredToBorderBox(
                box.Style, Math.Max(0, minSlot.AsLengthPx()), chrome);
            borderBoxSize = Math.Max(borderBoxSize, minBorderBox);
        }
        else if (minSlot.Tag == ComputedSlotTag.Percentage && definiteContaining)
        {
            // §10.4 — a definite `%` min-* resolves against the containing size; an
            // indefinite one computes to `0` (handled by the definiteContaining gate).
            var minBorderBox = BoxSizingHelper.DeclaredToBorderBox(
                box.Style, Math.Max(0, minSlot.AsPercentage() / 100.0 * containingSize), chrome);
            borderBoxSize = Math.Max(borderBoxSize, minBorderBox);
        }
        return borderBoxSize;
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

/// <summary>Per Phase 3 Task 19 cycle 1 — typed decode of
/// <see cref="PropertyId.Position"/> per CSS Positioned Layout L3 §3.
/// Values match the source-gen'd keyword table order
/// (<c>static / relative / absolute / fixed / sticky</c>) so the byte
/// value equals the keyword id.
///
/// <para>Cycle 1 implements <see cref="Absolute"/> placement against an
/// explicitly-sized box anchored to the establishing block's content
/// box. <see cref="Relative"/> only matters cycle-1 as a containing-
/// block establisher (its own offset application is a separate slice);
/// <see cref="Fixed"/> ships in Task 20; <see cref="Sticky"/> is
/// post-v1.</para></summary>
internal enum PositionValue : byte
{
    Static = 0,
    Relative = 1,
    Absolute = 2,
    Fixed = 3,
    Sticky = 4,
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
/// left | right]]</c>); L2 collapses this to six effective base behaviors
/// covering the common flexbox main-axis alignment patterns. Logical-
/// axis aliases (<c>start</c> / <c>end</c>) and directional aliases
/// (<c>left</c> / <c>right</c>) map to <see cref="FlexStart"/> /
/// <see cref="FlexEnd"/> under the L1 default LTR +
/// <c>flex-direction: row</c>; writing-mode-aware mapping is L3+ scope.
///
/// <para>Per Phase 3 Task 15 L2 post-PR-#62 review hardening F#1 — the
/// <c>safe</c> / <c>unsafe</c> overflow modifiers (compound keywords
/// like <c>safe center</c>) now carry their semantics on the separate
/// <see cref="OverflowAlignmentMode"/> channel returned alongside this
/// enum from <see cref="ComputedStyleLayoutExtensions.ReadJustifyContent"/>
/// (= <see cref="ResolvedJustifyContent"/>). The base position +
/// overflow mode together let
/// <c>FlexLayouter.ComputeJustifyContentOffsets</c> apply spec-correct
/// overflow handling per CSS Box Alignment L3 §5.3.</para></summary>
internal enum JustifyContentValue : byte
{
    FlexStart = 0,
    FlexEnd = 1,
    Center = 2,
    SpaceBetween = 3,
    SpaceAround = 4,
    SpaceEvenly = 5,
}

/// <summary>Per Phase 3 Task 15 L2 post-PR-#62 review hardening F#1 —
/// overflow-alignment mode per CSS Box Alignment L3 §5.3. The mode is
/// orthogonal to the base alignment value (<see cref="JustifyContentValue"/>)
/// and is decoded from the optional <c>&lt;overflow-position&gt;</c>
/// prefix of <c>justify-content</c>'s compound grammar (e.g.,
/// <c>safe center</c>, <c>unsafe flex-end</c>).
///
/// <para><b>Per spec.</b> <see cref="Default"/> (no modifier) — each
/// alignment family has its own overflow fallback per the spec (the
/// distribution values fall back to safe start; positional values keep
/// their natural offset which may be negative on overflow).
/// <see cref="Safe"/> — on overflow, fall back to "safe start"
/// regardless of the specified value (= no item is pushed below the
/// container's start edge). <see cref="Unsafe"/> — honor the specified
/// alignment even on overflow (items may be pushed offscreen).</para></summary>
internal enum OverflowAlignmentMode : byte
{
    Default = 0,
    Safe = 1,
    Unsafe = 2,
}

/// <summary>Per Phase 3 Task 15 L2 post-PR-#62 review hardening F#1 —
/// resolved <c>justify-content</c> value carrying both the base
/// alignment (<see cref="Value"/>) and the overflow modifier
/// (<see cref="Mode"/>). Returned by
/// <see cref="ComputedStyleLayoutExtensions.ReadJustifyContent"/>; the
/// two channels are consumed together by
/// <c>FlexLayouter.ComputeJustifyContentOffsets</c> per CSS Box
/// Alignment L3 §5.3.
///
/// <para>Pre-hardening the extension returned only <see cref="Value"/>
/// (= a bare <see cref="JustifyContentValue"/>); all 14 compound
/// keywords (safe X / unsafe X — indices 12-25) collapsed to
/// <see cref="JustifyContentValue.FlexStart"/>, hiding the spec's safe-
/// mode containment + unsafe-mode override semantics.</para></summary>
internal readonly record struct ResolvedJustifyContent(
    JustifyContentValue Value,
    OverflowAlignmentMode Mode);

/// <summary>Per Phase 3 Task 15 L3 — typed decode of
/// <see cref="PropertyId.AlignItems"/> per CSS Box Alignment L3 §6 +
/// CSS Flexbox L1 §8.3. L3 ships the four commonly-used position
/// values: <see cref="FlexStart"/> (cross-start pack), <see cref="FlexEnd"/>
/// (cross-end pack), <see cref="Center"/> (cross-axis center), and
/// <see cref="Stretch"/> (auto-resize items to fill the container's
/// cross extent — the computed default for <c>align-items: normal</c>
/// per CSS Flexbox L1 §8.3).
///
/// <para><b>L4+ deferrals.</b> <c>baseline</c> / <c>first baseline</c> /
/// <c>last baseline</c> require text-shaping integration; <c>anchor-center</c>
/// is CSS Anchor Positioning scope; <c>align-self</c> per-item override
/// adds a cascade read per item. All three families fall through to
/// <see cref="Stretch"/> (the safe default) in L3 — see
/// <c>docs/deferrals.md#flex-layouter-features</c>.</para>
///
/// <para>Logical-axis aliases (<c>start</c> / <c>end</c> / <c>self-start</c> /
/// <c>self-end</c>) map to <see cref="FlexStart"/> / <see cref="FlexEnd"/>
/// under the L1 default LTR + <c>flex-direction: row</c> — writing-mode-
/// aware mapping is L4+ scope.</para>
///
/// <para>The <c>safe</c> / <c>unsafe</c> overflow-position modifiers
/// (compound keywords like <c>safe center</c>) carry their semantics
/// on the separate <see cref="OverflowAlignmentMode"/> channel of
/// <see cref="ResolvedAlignItems"/>; mirrors L2's two-channel pattern
/// for <c>justify-content</c>.</para></summary>
internal enum AlignItemsValue : byte
{
    FlexStart = 0,
    FlexEnd = 1,
    Center = 2,
    Stretch = 3,
}

/// <summary>Per Phase 3 Task 15 L3 — resolved <c>align-items</c> value
/// carrying both the base alignment (<see cref="Value"/>) and the
/// overflow modifier (<see cref="Mode"/>). Returned by
/// <see cref="ComputedStyleLayoutExtensions.ReadAlignItems"/>; the two
/// channels are consumed together by
/// <c>FlexLayouter.ComputeAlignItemsPlacement</c> per CSS Box Alignment
/// L3 §5.3. Mirrors <see cref="ResolvedJustifyContent"/>'s shape so the
/// FlexLayouter has a single readable pattern for both axes.</summary>
internal readonly record struct ResolvedAlignItems(
    AlignItemsValue Value,
    OverflowAlignmentMode Mode);

/// <summary>Per Phase 3 Task 15 L9 — typed decode of
/// <see cref="PropertyId.AlignSelf"/> per CSS Box Alignment L3 §4.3.
/// Mirrors <see cref="AlignItemsValue"/> but ADDS the <c>auto</c>
/// variant that signals "defer to the container's
/// <c>align-items</c>". Per CSS Box Alignment §4.3: "If the value of
/// align-self is auto, its used value is the value of align-items on
/// the parent". The L1-L8 behavior is the auto case — every item
/// inherits the container's align-items unless it declares its own
/// align-self.
///
/// <para><b>L9 ships</b> the five effective values needed for the
/// per-item override: <see cref="Auto"/> (defer to container),
/// <see cref="FlexStart"/>, <see cref="FlexEnd"/>,
/// <see cref="Center"/>, <see cref="Stretch"/>. Logical-axis aliases
/// (<c>start</c> / <c>end</c> / <c>self-start</c> / <c>self-end</c>)
/// + directional aliases (<c>left</c> / <c>right</c>) map to
/// <c>flex-start</c> / <c>flex-end</c> under the L1 default LTR +
/// <c>flex-direction: row</c> (writing-mode-aware mapping is L10+
/// scope). The three <c>&lt;baseline-position&gt;</c> keywords
/// (<c>baseline</c> / <c>first baseline</c> / <c>last baseline</c>)
/// approximate to <see cref="Stretch"/> — mirrors
/// <see cref="ComputedStyleLayoutExtensions.ReadAlignItems"/>'s
/// baseline approximation; proper baseline alignment is L10+ scope
/// (text-shaping integration).</para>
/// </summary>
internal enum AlignSelfValue : byte
{
    Auto = 0,
    FlexStart = 1,
    FlexEnd = 2,
    Center = 3,
    Stretch = 4,
}

/// <summary>Per Phase 3 Task 15 L9 — resolved <c>align-self</c> value
/// carrying both the per-item alignment value (<see cref="Value"/>) and
/// the overflow modifier (<see cref="Mode"/>). Returned by
/// <see cref="ComputedStyleLayoutExtensions.ReadAlignSelf"/>; resolves
/// against the container's <see cref="ResolvedAlignItems"/> via
/// <see cref="ComputedStyleLayoutExtensions.ResolveAgainstContainerAlignItems"/>
/// to produce the effective alignment for one item.
///
/// <para><b>Composition with align-items.</b> The flow is:
/// (a) read the container's <c>align-items</c> once per AttemptLayout;
/// (b) for each item, read its <c>align-self</c>;
/// (c) call <see cref="ComputedStyleLayoutExtensions.ResolveAgainstContainerAlignItems"/>
/// to get the effective per-item <see cref="ResolvedAlignItems"/>;
/// (d) pass the effective value into the existing
/// <c>ComputeAlignItemsPlacement</c> helper. Items with
/// <see cref="AlignSelfValue.Auto"/> get the container's full
/// (value, mode) tuple unchanged — preserving the L1-L8 behavior.</para>
/// </summary>
internal readonly record struct ResolvedAlignSelf(
    AlignSelfValue Value,
    OverflowAlignmentMode Mode);

/// <summary>Per Phase 3 Task 15 L7 — typed decode of
/// <see cref="PropertyId.AlignContent"/> per CSS Box Alignment L3 §6 +
/// CSS Flexbox L1 §8.4. L7 ships the seven base values needed to
/// implement multi-line cross-axis distribution: positional values
/// (<see cref="FlexStart"/>, <see cref="FlexEnd"/>, <see cref="Center"/>),
/// distribution values (<see cref="SpaceBetween"/>, <see cref="SpaceAround"/>,
/// <see cref="SpaceEvenly"/>), and <see cref="Stretch"/> (the computed
/// default for <c>align-content: normal</c> per §8.4 — lines grow their
/// cross extents to fill the container).
///
/// <para><b>Single-line semantics.</b> Per CSS Flexbox L1 §8.4
/// align-content has NO EFFECT on a single-line container (= flex-wrap:
/// nowrap or wrapping with only one resulting line). The FlexLayouter
/// gates application on <c>lineCount &gt; 1</c>.</para>
///
/// <para><b>L7+ deferrals.</b> The <c>safe</c> / <c>unsafe</c> overflow
/// modifiers decode to <see cref="OverflowAlignmentMode"/> values but L7
/// applies a single safe-start fallback across all overflow modes (=
/// when sum-of-line-extents &gt; container cross-extent, lines stack at
/// cross-start at their natural extents). Fine-grained safe-vs-unsafe-
/// vs-default overflow rules per CSS Box Alignment L3 §5.3 are deferred
/// to L8+ — see <c>docs/deferrals.md#flex-layouter-features</c>. The
/// logical-axis aliases <c>start</c> / <c>end</c> + the directional
/// aliases <c>left</c> / <c>right</c> map to <see cref="FlexStart"/> /
/// <see cref="FlexEnd"/> under the L1 default LTR +
/// <c>flex-direction: row</c>; writing-mode-aware mapping is L8+ scope.</para></summary>
internal enum AlignContentValue : byte
{
    FlexStart = 0,
    FlexEnd = 1,
    Center = 2,
    SpaceBetween = 3,
    SpaceAround = 4,
    SpaceEvenly = 5,
    Stretch = 6,
}

/// <summary>Per Phase 3 Task 15 L7 — resolved <c>align-content</c> value
/// carrying both the base alignment (<see cref="Value"/>) and the
/// overflow modifier (<see cref="Mode"/>). Returned by
/// <see cref="ComputedStyleLayoutExtensions.ReadAlignContent"/>; the two
/// channels are consumed together by
/// <c>FlexLayouter.ComputeAlignContentOffsets</c>. Mirrors
/// <see cref="ResolvedJustifyContent"/>'s + <see cref="ResolvedAlignItems"/>'s
/// shape so the FlexLayouter has a uniform pattern for the three
/// alignment properties.</summary>
internal readonly record struct ResolvedAlignContent(
    AlignContentValue Value,
    OverflowAlignmentMode Mode);

/// <summary>Per Phase 3 Task 15 L4 + L5 — typed decode of
/// <see cref="PropertyId.FlexDirection"/> per CSS Flexbox L1 §5.1.
/// L4 ships <c>row</c> (the default; main axis = inline axis) and
/// <c>column</c> (main axis = block axis); L5 ships the reversed
/// variants <c>row-reverse</c> + <c>column-reverse</c>, which per §5.1
/// "swap main-start and main-end" along the same row / column axis.
/// The reversal is orthogonal to the row/column axis swap shipped in
/// L4 — the L5 implementation applies a single offset-flip transform
/// at the FlexLayouter's emission site, leaving L4's axis-mapping
/// layer untouched.
///
/// <para>Per CSS Flexbox L1 §5.1 the four values control:
/// <list type="bullet">
///   <item><see cref="Row"/> — main = inline (default); items flow
///   left-to-right under LTR.</item>
///   <item><see cref="RowReverse"/> — main = inline, items flow
///   right-to-left under LTR (L5 — items pack at the inline-end edge
///   in reverse DOM order).</item>
///   <item><see cref="Column"/> — main = block; items stack top-to-
///   bottom.</item>
///   <item><see cref="ColumnReverse"/> — main = block, items stack
///   bottom-to-top (L5 — items pack at the block-end edge in reverse
///   DOM order).</item>
/// </list></para></summary>
internal enum FlexDirectionValue : byte
{
    Row = 0,
    RowReverse = 1,
    Column = 2,
    ColumnReverse = 3,
}

/// <summary>Per Phase 3 Task 15 L4 — helper extensions on
/// <see cref="FlexDirectionValue"/>.</summary>
internal static class FlexDirectionValueExtensions
{
    /// <summary>Per Phase 3 Task 15 L4 — is this a column direction?
    /// Used for axis selection in <c>FlexLayouter</c> +
    /// <c>BlockLayouter</c>'s flex pre-measure dispatch.
    /// <c>row-reverse</c> remains a row direction (main = inline);
    /// <c>column-reverse</c> remains a column direction (main = block).
    /// The L5+ reversal handling is orthogonal to this predicate — the
    /// L5 reversal logic reads <see cref="IsFlexReverseDirection"/> to
    /// distinguish the two row variants + the two column variants.</summary>
    public static bool IsFlexColumnDirection(this FlexDirectionValue value)
        => value == FlexDirectionValue.Column || value == FlexDirectionValue.ColumnReverse;

    /// <summary>Per Phase 3 Task 15 L5 — is this a reverse direction?
    /// Per CSS Flexbox L1 §5.1, <c>row-reverse</c> and
    /// <c>column-reverse</c> "swap main-start and main-end". Item
    /// per-emission math (cross-axis alignment, stretch, the
    /// <c>justify-content</c> start-offset + between-spacing) is
    /// UNCHANGED; only the main-axis ORIGIN flips. The L5 implementation
    /// applies a single offset-flip transform at the emission site
    /// IN FLEXLAYOUTER (not by this predicate itself); the formula
    /// accounts for the wrapper's content-box origin:
    /// <c>actualMainOffset = (contentMainOffset + containerMainSize) -
    /// (mainCursor - contentMainOffset) - itemMainSize</c>. The
    /// <c>contentMainOffset</c> term is the wrapper's content-box
    /// start on the main axis (= padding/border-aware origin). The
    /// natural-direction (row / column) algorithm produces both the
    /// items' end-edge placements + the reversed visual ordering in
    /// one pass. See <c>FlexLayouter.cs</c>'s emission loop for the
    /// applied transform.
    ///
    /// <para><b>Reversal semantics.</b>
    /// <list type="bullet">
    ///   <item><c>row-reverse</c> — main = inline (still), but main-start
    ///   moves to the inline-end edge; under LTR the first DOM item
    ///   appears at the right edge.</item>
    ///   <item><c>column-reverse</c> — main = block (still), but
    ///   main-start moves to the block-end edge; the first DOM item
    ///   appears at the bottom of the container.</item>
    /// </list>
    /// Cross-axis behavior is unchanged: <c>flex-direction: row-reverse</c>
    /// still has block as cross axis; <c>column-reverse</c> still has
    /// inline as cross axis. <c>justify-content</c> values honor the
    /// reversed main axis — <c>flex-start</c> packs at the (reversed)
    /// main-start, which is visually the right edge for
    /// <c>row-reverse</c> and the bottom edge for <c>column-reverse</c>.</para></summary>
    public static bool IsFlexReverseDirection(this FlexDirectionValue value)
        => value == FlexDirectionValue.RowReverse
            || value == FlexDirectionValue.ColumnReverse;

    /// <summary>Per Phase 3 Task 16 cycle 4c post-PR-#84 review P3 #5
    /// — return the property IDs to read for an item's main-axis +
    /// cross-axis sizes given the resolved <c>flex-direction</c>.
    /// <list type="bullet">
    ///   <item><c>row</c> / <c>row-reverse</c>: main = inline
    ///   (<c>width</c>); cross = block (<c>height</c>).</item>
    ///   <item><c>column</c> / <c>column-reverse</c>: main = block
    ///   (<c>height</c>); cross = inline (<c>width</c>).</item>
    /// </list>
    /// The reversed variants share the same axis assignment as their
    /// non-reversed counterparts per CSS Flexbox L1 §5.1 — reversal
    /// only swaps main-start and main-end edges, not the row/column
    /// axis itself. The reversal logic flips the main-axis offset at
    /// the emission site in <see cref="FlexLayouter"/>; the property
    /// reads here stay direction-agnostic.
    ///
    /// <para><b>Pre-cycle-4c (PR-#84 review P3 #5):</b> this mapping
    /// lived as duplicate methods in <see cref="FlexLayouter"/>
    /// (<c>GetAxisProperties</c>) + <see cref="FlexLinePacker.Pack"/>
    /// (an inline ternary). The duplication created drift risk —
    /// e.g., a writing-mode-aware axis-mapping update would need
    /// edits in both places. The shared extension lives here next to
    /// the other <see cref="FlexDirectionValue"/> helpers
    /// (<see cref="IsFlexColumnDirection"/>,
    /// <see cref="IsFlexReverseDirection"/>) so future axis updates
    /// touch ONE site.</para></summary>
    public static (PropertyId mainSize, PropertyId crossSize) GetAxisProperties(
        this FlexDirectionValue value)
        => value.IsFlexColumnDirection()
            ? (PropertyId.Height, PropertyId.Width)
            : (PropertyId.Width, PropertyId.Height);
}

/// <summary>Per Phase 3 Task 15 L6 — helper extensions on
/// <see cref="FlexWrapValue"/>. Separated from
/// <see cref="FlexDirectionValueExtensions"/> per the PR-#66 Copilot
/// review (#3271026295 + #3271095597) so the extension class name
/// matches the type it extends.</summary>
internal static class FlexWrapValueExtensions
{
    /// <summary>Per Phase 3 Task 15 L6 — is this flex container
    /// requesting multi-line layout? Returns <see langword="true"/> for
    /// both <see cref="FlexWrapValue.Wrap"/> and
    /// <see cref="FlexWrapValue.WrapReverse"/>. For L6 the two values
    /// behave identically at the layouter — <c>wrap-reverse</c>'s
    /// cross-axis line-stacking reversal is L7+ scope and is not yet
    /// applied (a <c>LAYOUT-FLEX-WRAP-REVERSE-APPROXIMATED-001</c>
    /// Warning surfaces the gap). The split exists so an L7+ addition
    /// can branch on the raw <see cref="FlexWrapValue"/> while every
    /// gate that just needs "single-line vs. multi-line" can use this
    /// predicate.</summary>
    public static bool IsFlexWrapping(this FlexWrapValue value)
        => value == FlexWrapValue.Wrap || value == FlexWrapValue.WrapReverse;
}

/// <summary>Per Phase 3 Task 15 L6 — typed decode of
/// <see cref="PropertyId.FlexWrap"/> per CSS Flexbox L1 §6.3. L6 ships
/// the multi-line algorithm for <see cref="Wrap"/>; <see cref="NoWrap"/>
/// preserves the L1-L5 single-line behavior.
///
/// <para><b>WrapReverse handling (L6).</b> <see cref="WrapReverse"/>
/// decodes successfully so the cascade carries the authored value, but
/// at the FlexLayouter L6 treats <see cref="WrapReverse"/> identically
/// to <see cref="Wrap"/> — the cross-axis line-stacking reversal
/// (CSS Flexbox L1 §6.3 "wrap-reverse: same as wrap but the
/// cross-start and cross-end directions are swapped") is L7+ scope.
/// See <c>docs/deferrals.md#flex-layouter-features</c>. The split keeps
/// the cascade slot lossless so when L7+ adds the cross-axis reversal,
/// pre-authored <c>flex-wrap: wrap-reverse</c> declarations activate
/// the new behavior without a re-author.</para></summary>
internal enum FlexWrapValue : byte
{
    NoWrap = 0,
    Wrap = 1,
    WrapReverse = 2,
}

/// <summary>Per Phase 3 Task 15 L8 — discriminator for the
/// <c>flex-basis</c> property's value per CSS Flexbox L1 §7.2. The
/// grammar admits four families:
/// <list type="bullet">
///   <item><see cref="Auto"/> — <c>flex-basis: auto</c> (the §7.2
///   default). Delegates to the item's declared main-size
///   (<c>width</c> for row, <c>height</c> for column). When the main-
///   size is also <c>auto</c>, the hypothetical main-size is the
///   item's intrinsic content size (= 0 for L8 since intrinsic sizing
///   isn't wired yet).</item>
///   <item><see cref="Content"/> — <c>flex-basis: content</c>. Per
///   spec, forces the item's intrinsic content size regardless of the
///   declared main-size (§7.2.1). <b>L8 KNOWN APPROXIMATION:</b> L8
///   delegates Content to the same path as Auto (= reads the declared
///   main-size). This is OBSERVABLY WRONG when an item has an explicit
///   <c>width</c> AND a non-zero intrinsic content size — the spec
///   says Content should ignore the declared width and use the
///   intrinsic content size; L8 returns the declared width. The
///   variant is preserved on the resolved struct so the L9+ intrinsic-
///   sizing integration activates without a re-author. Pinned by
///   <c>L8_known_gap_flex_basis_content_approximates_to_auto</c>.</item>
///   <item><see cref="LengthPx"/> — <c>flex-basis: &lt;length&gt;</c>
///   (e.g., <c>flex-basis: 100px</c>). Uses the resolved pixel value
///   as the hypothetical main-size, ignoring the item's declared
///   main-size.</item>
///   <item><see cref="Percentage"/> — <c>flex-basis:
///   &lt;percentage&gt;</c> (e.g., <c>flex-basis: 50%</c>). Resolves
///   against the container's main-size. Per §9.2.3 a percentage
///   flex-basis is treated as <c>auto</c> when the container's main-
///   size is indefinite; L8 only supports definite-main-size containers
///   (= the FlexLayouter contract already requires a definite container
///   main-size from BlockLayouter).</item>
/// </list>
/// The min-content / max-content / fit-content keywords + the
/// fit-content(<c>length-percentage</c>) function are L9+ scope —
/// they require intrinsic-sizing integration.</summary>
internal enum FlexBasisKind : byte
{
    Auto = 0,
    Content = 1,
    LengthPx = 2,
    Percentage = 3,
}

/// <summary>Per Phase 3 Task 15 L8 — resolved <c>flex-basis</c> value
/// returned by <see cref="ComputedStyleLayoutExtensions.ReadFlexBasis"/>.
/// The <see cref="Kind"/> discriminates four families per CSS Flexbox
/// L1 §7.2; <see cref="Value"/> carries the pixel length for
/// <see cref="FlexBasisKind.LengthPx"/> or the percentage (raw, NOT
/// divided by 100 — e.g., <c>flex-basis: 50%</c> yields <c>50.0</c>)
/// for <see cref="FlexBasisKind.Percentage"/>. For
/// <see cref="FlexBasisKind.Auto"/> and <see cref="FlexBasisKind.Content"/>
/// the <see cref="Value"/> field is unused (defaults to 0).
///
/// <para><b>Resolution to hypothetical main-size.</b> The FlexLayouter
/// resolves Auto and Content variants against the item's declared
/// main-size (or 0 when the main-size is also auto, since intrinsic
/// sizing is L9+ scope). Percentage resolves against the container's
/// main-size at the per-line resolution pass; LengthPx is used
/// directly. The hypothetical main-size feeds the §9.7 flexibility
/// algorithm (grow + shrink resolution).</para></summary>
internal readonly record struct ResolvedFlexBasis(
    FlexBasisKind Kind,
    double Value);
