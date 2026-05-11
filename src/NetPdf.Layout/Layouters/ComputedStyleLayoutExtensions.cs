// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

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
