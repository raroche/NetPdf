// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Resolves CSS keyword values per CSS Values L4 §4.1 against per-property keyword
/// tables. Each <see cref="PropertyType.Keyword"/> property has a closed set of
/// admitted identifiers; this resolver maps the input string (ASCII case-insensitive
/// per CSS Syntax L3) to a stable small-integer id stored in
/// <see cref="ComputedSlot.FromKeyword"/>. Downstream stages (BoxBuilder, layout)
/// switch on the id without needing to re-parse the source text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyword id stability.</b> The ids assigned per property are part of the internal
/// contract between cascade and downstream consumers. They're zero-based, dense, and
/// never change for an existing keyword once shipped — adding a new admitted keyword
/// appends a new id at the end. Renaming or reordering would silently break BoxBuilder.
/// </para>
/// <para>
/// <b>Cycle 1 coverage.</b> Tables are populated for the 24 keyword-typed properties
/// in <c>properties.json</c>. Unrecognized keywords for a covered property emit
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/>. Values that resemble
/// CSS-wide keywords (<c>inherit</c>, <c>initial</c>, <c>unset</c>, <c>revert</c>,
/// <c>revert-layer</c>) are NOT this resolver's job — the cascade handles them at
/// declaration assignment time and they never reach a per-property resolver.
/// </para>
/// </remarks>
internal static class KeywordResolver
{
    public static ResolverResult Resolve(
        string value,
        PropertyId propertyId,
        string propertyName,
        ICssDiagnosticsSink? diagnostics,
        CssSourceLocation location)
    {
        if (!Tables.TryGetValue(propertyId, out var table))
        {
            // No table registered for this Keyword-typed property — UnsupportedUnvalidated
            // (NOT Deferred) because we haven't validated the value against the property's
            // admitted-keyword set. Cycle-2 keyword tables upgrade these to Resolved /
            // Invalid as appropriate.
            return ResolverResult.UnsupportedUnvalidated(value);
        }
        var normalized = NormalizeKeywordWhitespace(value);
        if (table.TryGetValue(normalized, out var id))
            return ResolverResult.Resolved(ComputedSlot.FromKeyword(id));

        // Per Phase A A-6 — sanitize untrusted value before message interpolation.
        // Property names are generator-validated (frozen-set lookup); raw value is
        // author CSS so it could carry C0/C1/DEL chars or extreme length.
        var safeValue = DiagnosticTextSanitizer.Sanitize(value);
        diagnostics?.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssPropertyValueInvalid001,
            $"'{propertyName}: {safeValue}' — '{safeValue}' is not an admitted keyword for this property.",
            CssDiagnosticSeverity.Warning,
            location));
        return ResolverResult.Invalid();
    }

    /// <summary>Try to look up a keyword id without dispatching through the resolver
    /// (used by tests + by future composite-type resolvers that branch on a keyword
    /// alternative).</summary>
    public static bool TryGetId(PropertyId propertyId, string keyword, out int id)
    {
        id = -1;
        if (!Tables.TryGetValue(propertyId, out var table)) return false;
        return table.TryGetValue(NormalizeKeywordWhitespace(keyword), out id);
    }

    /// <summary>Lower-case + collapse interior runs of ASCII whitespace into a single
    /// space + trim edges. Required for compound keywords like <c>first baseline</c>
    /// or <c>safe center</c> per CSS Box Alignment 3 §4.4 — authors may write any
    /// amount of whitespace between the parts.</summary>
    private static string NormalizeKeywordWhitespace(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new StringBuilder(raw.Length);
        var prevWs = true; // suppress leading whitespace
        foreach (var ch in raw)
        {
            var c = ch;
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                if (!prevWs) sb.Append(' ');
                prevWs = true;
                continue;
            }
            // ASCII case-fold inline.
            if (c is >= 'A' and <= 'Z') c = (char)(c + 32);
            sb.Append(c);
            prevWs = false;
        }
        // Trim trailing space we may have appended.
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    // The Box-Alignment helper arrays MUST be declared before Tables — Tables's
    // initializer calls BuildAlignItemsTable() etc., which reference them. C#
    // static-field initializers run in textual declaration order; a forward
    // reference produces null at access time (cctor NullReferenceException).
    /// <summary>The seven <c>&lt;self-position&gt;</c> values per CSS Box Alignment 3 §4.</summary>
    private static readonly string[] SelfPositions =
        { "center", "start", "end", "self-start", "self-end", "flex-start", "flex-end" };

    /// <summary>The <c>&lt;content-position&gt;</c> set per CSS Box Alignment 3 §4
    /// (note: NO self-start / self-end here — those are <c>&lt;self-position&gt;</c>
    /// only). <c>left</c> + <c>right</c> are content-position-specific extensions
    /// for justify-content.</summary>
    private static readonly string[] ContentPositions =
        { "center", "start", "end", "flex-start", "flex-end", "left", "right" };

    /// <summary>The <c>&lt;content-distribution&gt;</c> set per §4.5.</summary>
    private static readonly string[] ContentDistributions =
        { "space-between", "space-around", "space-evenly", "stretch" };

    /// <summary>The per-property keyword tables. Each property maps every admitted
    /// keyword (lower-case) to a stable zero-based id. Populated at module load.</summary>
    private static readonly FrozenDictionary<PropertyId, FrozenDictionary<string, int>> Tables =
        BuildAllTables();

    private static FrozenDictionary<PropertyId, FrozenDictionary<string, int>> BuildAllTables()
    {
        var b = new Dictionary<PropertyId, FrozenDictionary<string, int>>();

        // align-content / align-items / align-self / justify-content — CSS Box
        // Alignment L3 §4. Per §4.4: <baseline-position> is `[first | last]? baseline`
        // — bare `first`/`last` are NOT valid, only the compound `first baseline` /
        // `last baseline`. Per §4.5: <self-position> + optional <overflow-position>
        // (safe / unsafe) gives the full positional grid. The compound forms are
        // looked up via whitespace-normalized matching (see NormalizeKeywordWhitespace).
        // align-content + justify-content share the <content-position> +
        // <content-distribution> grammar; align-items + align-self share the
        // <self-position> grammar (with align-self additionally admitting `auto`).
        b[PropertyId.AlignContent] = BuildAlignContentTable();
        b[PropertyId.AlignItems] = BuildAlignItemsTable();
        b[PropertyId.AlignSelf] = BuildAlignSelfTable();
        b[PropertyId.JustifyContent] = BuildJustifyContentTable();

        // Border style — CSS Backgrounds & Borders 3 §4.4.
        var borderStyle = T("none", "hidden", "dotted", "dashed", "solid", "double",
            "groove", "ridge", "inset", "outset");
        b[PropertyId.BorderTopStyle] = borderStyle;
        b[PropertyId.BorderRightStyle] = borderStyle;
        b[PropertyId.BorderBottomStyle] = borderStyle;
        b[PropertyId.BorderLeftStyle] = borderStyle;

        // outline-style — CSS UI 4 §5.2 (outline cycle; post-PR-#173 review P2): `auto |
        // <outline-line-style>`, where <outline-line-style> is the border-style values EXCEPT `hidden`
        // (which is invalid for an outline). So `@supports (outline-style: hidden)` is FALSE and a later
        // `outline-style: hidden` can't suppress an earlier valid value. Indices (none=0, solid=3,
        // auto=9) are the FragmentPainter outline constants; `none` paints nothing, `auto` paints solid.
        b[PropertyId.OutlineStyle] = T("none", "dotted", "dashed", "solid", "double",
            "groove", "ridge", "inset", "outset", "auto");

        // background-origin / background-clip — CSS B&B §3.4–§3.5 (bg-origin / bg-clip cycles): the
        // positioning / painting box. Registered (PR #170 review P2) so @supports reports them and
        // an invalid value is diagnosed; the tiler reads the resolved keyword.
        b[PropertyId.BackgroundOrigin] = T("border-box", "padding-box", "content-box");
        b[PropertyId.BackgroundClip] = T("border-box", "padding-box", "content-box");

        // background-attachment — CSS B&B §3.10 (bg-attachment cycle): scroll[initial]/fixed/local.
        // Registered for validation only: @supports reports it and an INVALID value is diagnosed, but
        // rendering does not yet consume the value — PARSE-ONLY metadata for now. For PAGED media there
        // is no scroll, so every value paints element-relative; `fixed` page-relative positioning is a
        // documented deferral (deferrals.md), silently approximated (NOT diagnosed — the value is valid).
        b[PropertyId.BackgroundAttachment] = T("scroll", "fixed", "local");

        // border-collapse — CSS Tables 3 §6.
        b[PropertyId.BorderCollapse] = T("separate", "collapse");

        // box-sizing — CSS Basic UI 4 §10.
        b[PropertyId.BoxSizing] = T("content-box", "border-box");

        // break-before / break-after — CSS Fragmentation 3 §3.1. Forced-break
        // values: page / left / right / recto / verso / column / region / all;
        // avoid values: avoid / avoid-page / avoid-column / avoid-region. Index
        // order is the contract read by ComputedStyleLayoutExtensions' break helpers.
        b[PropertyId.BreakBefore] = T(
            "auto", "avoid", "avoid-page", "avoid-column", "avoid-region",
            "page", "left", "right", "recto", "verso", "column", "region", "all");
        b[PropertyId.BreakAfter] = T(
            "auto", "avoid", "avoid-page", "avoid-column", "avoid-region",
            "page", "left", "right", "recto", "verso", "column", "region", "all");

        // page-break-before / page-break-after — CSS 2.1 §13.3.1 legacy aliases
        // (CSS Fragmentation 3 §A maps `always`→`page`, `avoid`→`avoid`, etc.).
        b[PropertyId.PageBreakBefore] = T("auto", "always", "avoid", "left", "right");
        b[PropertyId.PageBreakAfter] = T("auto", "always", "avoid", "left", "right");

        // break-inside — CSS Fragmentation 3 §3.2.
        b[PropertyId.BreakInside] = T("auto", "avoid", "avoid-page", "avoid-column", "avoid-region");

        // caption-side — CSS Tables 3 §11.5.2 + CSS Logical Properties 1 §4.4.
        // Physical: top, bottom. Writing-mode-relative: block-start, block-end,
        // inline-start, inline-end. Phase 3 Task 12 sub-cycle 3 maps the
        // writing-mode-relative keywords through to the physical axes via LTR-
        // only mapping (block-start → top, block-end → bottom); the inline-
        // axis keywords are admitted by the keyword table but the layout-side
        // ReadCaptionSide reader falls back to top (RTL + vertical writing-mode
        // support is deferred to sub-cycle 4+ writing-mode work).
        b[PropertyId.CaptionSide] = T("top", "bottom",
            "block-start", "block-end", "inline-start", "inline-end");

        // clear — CSS 2.2 §9.5.2 (still active in L3).
        b[PropertyId.Clear] = T("none", "left", "right", "both", "inline-start", "inline-end");

        // cursor — CSS Basic UI 4 §8.1.1 (subset of admitted bare keywords; URL forms
        // are out-of-scope for the keyword resolver).
        b[PropertyId.Cursor] = T("auto", "default", "none", "context-menu", "help", "pointer",
            "progress", "wait", "cell", "crosshair", "text", "vertical-text", "alias", "copy",
            "move", "no-drop", "not-allowed", "grab", "grabbing", "all-scroll", "col-resize",
            "row-resize", "n-resize", "e-resize", "s-resize", "w-resize", "ne-resize",
            "nw-resize", "se-resize", "sw-resize", "ew-resize", "ns-resize", "nesw-resize",
            "nwse-resize", "zoom-in", "zoom-out");

        // direction — CSS Writing Modes 4 §2.1. Sets the inline base direction of
        // the box's content. ids ltr=0, rtl=1 — the contract the layout-side
        // ComputedStyle.ReadDirection() / ReadParagraphDirection() readers switch on.
        b[PropertyId.Direction] = T("ltr", "rtl");

        // display — CSS Display 3 §2 (the common single-keyword forms; multi-keyword
        // syntax `inline flex` etc. is out-of-scope for cycle 1).
        b[PropertyId.Display] = T("block", "inline", "inline-block", "list-item", "flex",
            "inline-flex", "grid", "inline-grid", "flow-root", "table", "inline-table",
            "table-row-group", "table-header-group", "table-footer-group", "table-row",
            "table-cell", "table-column-group", "table-column", "table-caption",
            "ruby", "ruby-base", "ruby-text", "ruby-base-container", "ruby-text-container",
            "contents", "none");

        // flex-direction / flex-wrap — CSS Flexbox 1 §5.
        b[PropertyId.FlexDirection] = T("row", "row-reverse", "column", "column-reverse");
        b[PropertyId.FlexWrap] = T("nowrap", "wrap", "wrap-reverse");

        // float — CSS 2.2 §9.5.1.
        b[PropertyId.Float] = T("none", "left", "right", "inline-start", "inline-end");

        // font-style — CSS Fonts 4 §3.5 (single-keyword form; oblique-with-angle is
        // out-of-scope for cycle 1).
        b[PropertyId.FontStyle] = T("normal", "italic", "oblique");

        // grid-auto-flow — CSS Grid L1 §7.7. Phase 3 Task 18 cycle 6
        // shipped sparse `row` / `column`; cycle 7d adds the `dense`
        // modifier (= backtracking placement that fills earlier holes).
        // Per spec the grammar is `[ row | column ] || dense`, so
        // multiple authored forms decode to the same semantic value.
        // ID encoding:
        //   0 = row              (sparse row)
        //   1 = column           (sparse column)
        //   2 = row dense        (= "dense" alone, "row dense", "dense row")
        //   3 = column dense     (= "column dense", "dense column")
        b[PropertyId.GridAutoFlow] = BuildGridAutoFlowTable();

        // list-style-type — CSS Lists 3 §7.1 (subset of named counter-styles
        // shipped in cycle 1; full @counter-style support is post-v1).
        b[PropertyId.ListStyleType] = T("none", "disc", "circle", "square",
            "decimal", "decimal-leading-zero",
            "lower-roman", "upper-roman", "lower-alpha", "upper-alpha",
            "lower-latin", "upper-latin", "lower-greek");

        // list-style-position — CSS Lists 3 §6.
        b[PropertyId.ListStylePosition] = T("inside", "outside");

        // overflow — CSS Overflow 3 §3.1.
        var overflow = T("visible", "hidden", "clip", "scroll", "auto");
        b[PropertyId.OverflowX] = overflow;
        b[PropertyId.OverflowY] = overflow;

        // position — CSS Positioned Layout 3 §2.
        b[PropertyId.Position] = T("static", "relative", "absolute", "fixed", "sticky");

        // table-layout — CSS Tables 3 §3 + §3.5. Phase 3 Task 12 sub-
        // cycle 4 ships the `fixed` algorithm (per-column widths from
        // <col> / first-row cells); `auto` still uses the equal-split
        // approximation (the spec-strict shrink-to-fit min/max-content
        // algorithm is sub-cycle 5+ work — see
        // docs/deferrals.md#table-auto-fixed-spans-borders).
        b[PropertyId.TableLayout] = T("auto", "fixed");

        // column-fill — CSS Multi-column L1 §3.4. Cycle 1 parses the
        // value but doesn't act on it — columns fill serially
        // regardless. Sub-cycle 2+ will honor `balance` /
        // `balance-all`. See
        // docs/deferrals.md#multicol-balancing-pagination.
        b[PropertyId.ColumnFill] = T("balance", "balance-all", "auto");

        // column-rule-style — CSS Multi-column L1 §5.2. Uses the
        // same keyword set as border-style (CSS Backgrounds &
        // Borders 3 §4.4). Cycle 1 parses but doesn't paint the
        // column rule.
        b[PropertyId.ColumnRuleStyle] = borderStyle;

        // text-align — CSS Text 3 §7.1.
        b[PropertyId.TextAlign] = T("start", "end", "left", "right", "center", "justify",
            "match-parent", "justify-all");

        // text-decoration-line — CSS Text Decoration 3 §2 (single-keyword form; the
        // shorthand syntax allowing multiple lines like `underline overline` is
        // cycle-2 work).
        b[PropertyId.TextDecorationLine] = T("none", "underline", "overline", "line-through",
            "blink");

        // text-transform — CSS Text 3 §6.1.
        b[PropertyId.TextTransform] = T("none", "capitalize", "uppercase", "lowercase",
            "full-width", "full-size-kana");

        // white-space — CSS Text 3 §3.1 (legacy single-keyword form; the modern
        // `white-space: <white-space-collapse> || <text-wrap-mode>` shorthand is
        // out-of-scope for cycle 1).
        b[PropertyId.WhiteSpace] = T("normal", "pre", "nowrap", "pre-wrap", "break-spaces", "pre-line");

        // Per Phase 3 Task 10 cycle 2 — overflow-wrap (CSS Text 3 §5.1),
        // word-break (CSS Text 3 §5.2), hyphens (CSS Text 3 §6.1).
        // Wired through here for cascade resolution. The keyword id
        // is the source-of-truth (stored in ComputedSlot); the
        // layout-side enum mapping is performed by the cycle-2-review
        // materializer at `NetPdf.Layout.Inline.InlineTextPolicy` +
        // `InlineTextPolicyMaterializer.ReadInlineTextPolicy`. That
        // materializer also performs the cross-property alias folds
        // (overflow-wrap:break-word → anywhere; word-break:break-word
        // → word-break:normal + overflow-wrap:anywhere) that the
        // simple cast-to-enum approach can't express.
        b[PropertyId.OverflowWrap] = T("normal", "anywhere", "break-word");
        b[PropertyId.WordBreak] = T("normal", "break-all", "keep-all", "break-word");
        b[PropertyId.Hyphens] = T("none", "manual", "auto");

        // object-fit — CSS Images 3 §5.4 (object-fit cycle; index order is the painter's
        // contract: 0 = fill, the initial).
        b[PropertyId.ObjectFit] = T("fill", "contain", "cover", "none", "scale-down");

        return b.ToFrozenDictionary();
    }

    /// <summary>Build the align-items table per CSS Box Alignment 3 §4.4.
    /// Grammar: <c>normal | stretch | &lt;baseline-position&gt; |
    /// [&lt;overflow-position&gt;? &amp;&amp; &lt;self-position&gt;] | anchor-center</c>.</summary>
    private static FrozenDictionary<string, int> BuildAlignItemsTable()
    {
        var entries = new List<string>(32) { "normal", "stretch", "anchor-center" };
        // <baseline-position>: baseline | first baseline | last baseline.
        entries.Add("baseline");
        entries.Add("first baseline");
        entries.Add("last baseline");
        // <self-position> on its own.
        foreach (var p in SelfPositions) entries.Add(p);
        // <overflow-position> <self-position>.
        foreach (var op in new[] { "safe", "unsafe" })
            foreach (var p in SelfPositions) entries.Add($"{op} {p}");
        return T(entries.ToArray());
    }

    /// <summary>Build the align-self table per §4.3. Same as align-items plus
    /// <c>auto</c>.</summary>
    private static FrozenDictionary<string, int> BuildAlignSelfTable()
    {
        var entries = new List<string>(32) { "auto", "normal", "stretch", "anchor-center" };
        entries.Add("baseline");
        entries.Add("first baseline");
        entries.Add("last baseline");
        foreach (var p in SelfPositions) entries.Add(p);
        foreach (var op in new[] { "safe", "unsafe" })
            foreach (var p in SelfPositions) entries.Add($"{op} {p}");
        return T(entries.ToArray());
    }

    /// <summary>Build the justify-content table per §4.5.
    /// Grammar: <c>normal | &lt;content-distribution&gt; |
    /// [&lt;overflow-position&gt;? &amp;&amp; [&lt;content-position&gt; | left | right]]</c>.</summary>
    private static FrozenDictionary<string, int> BuildJustifyContentTable()
    {
        var entries = new List<string>(32) { "normal" };
        // <content-distribution>.
        foreach (var d in ContentDistributions) entries.Add(d);
        // <content-position> on its own.
        foreach (var p in ContentPositions) entries.Add(p);
        // <overflow-position> <content-position>.
        foreach (var op in new[] { "safe", "unsafe" })
            foreach (var p in ContentPositions) entries.Add($"{op} {p}");
        return T(entries.ToArray());
    }

    /// <summary>Per Phase 3 Task 15 L7 — build the align-content table per
    /// CSS Box Alignment L3 §4.5 + §6.3 (= same grammar as justify-content
    /// but applies to cross-axis line distribution per CSS Flexbox L1
    /// §8.4, plus the &lt;baseline-position&gt; family per §6.3). Grammar:
    /// <c>normal | &lt;baseline-position&gt; | &lt;content-distribution&gt; |
    /// [&lt;overflow-position&gt;? &amp;&amp; &lt;content-position&gt;]</c>.
    /// Like justify-content this uses <see cref="ContentPositions"/> +
    /// <see cref="ContentDistributions"/> rather than the
    /// <see cref="SelfPositions"/> set used by align-items / align-self,
    /// but ADDS the three baseline keywords (per §6.3 align-content
    /// admits &lt;baseline-position&gt;, same as align-items).
    ///
    /// <para><b>Note on <c>left</c> / <c>right</c> in the position set.</b>
    /// <see cref="ContentPositions"/> includes <c>left</c> + <c>right</c>
    /// per CSS Box Alignment L3 §4.5 (the &lt;content-position&gt;
    /// production admits <c>center | start | end | flex-start | flex-end |
    /// left | right</c>). For <c>align-content</c> per §6.3 the directional
    /// keywords <c>left</c> / <c>right</c> compute to <c>start</c>
    /// (justify-content uses the same set but with axis-meaningful
    /// behavior); we admit them here uniformly with the other content-
    /// positions so the cascade slot is lossless, and the reader
    /// (<c>ReadAlignContent</c>) maps them to <c>flex-start</c> /
    /// <c>flex-end</c> under the L1 default LTR + horizontal-tb writing-
    /// mode (writing-mode-aware mapping is L8+ scope).</para>
    ///
    /// <para><b>Index layout (Phase 3 Task 15 L7 post-PR-#67 F#6 — baseline
    /// added).</b> 0=normal, 1-4=&lt;content-distribution&gt;
    /// (space-between, space-around, space-evenly, stretch),
    /// 5-7=&lt;baseline-position&gt; (baseline, first baseline, last
    /// baseline), 8-14=&lt;content-position&gt; (center, start, end,
    /// flex-start, flex-end, left, right), 15-21=safe X (same 7
    /// positions), 22-28=unsafe X. Total = 29 entries.</para></summary>
    private static FrozenDictionary<string, int> BuildAlignContentTable()
    {
        var entries = new List<string>(32) { "normal" };
        // <content-distribution> — indices 1-4.
        foreach (var d in ContentDistributions) entries.Add(d);
        // <baseline-position> per §6.3 — indices 5-7. Mirrors the
        // align-items grammar (BuildAlignItemsTable) which also admits
        // these three keywords. L7 approximates them as Stretch (the
        // safe default) at the reader level; proper baseline alignment
        // is text-shaping integration scope (L8+).
        entries.Add("baseline");
        entries.Add("first baseline");
        entries.Add("last baseline");
        // <content-position> on its own — indices 8-14.
        foreach (var p in ContentPositions) entries.Add(p);
        // <overflow-position> <content-position> — indices 15-21 (safe)
        // + 22-28 (unsafe).
        foreach (var op in new[] { "safe", "unsafe" })
            foreach (var p in ContentPositions) entries.Add($"{op} {p}");
        return T(entries.ToArray());
    }

    /// <summary>Build a keyword → id table from a sequence of admitted keywords.
    /// Ids are zero-based + dense in argument order so each entry's id matches its
    /// position in the source code (handy for downstream switches).</summary>
    private static FrozenDictionary<string, int> T(params string[] keywords)
    {
        var dict = new Dictionary<string, int>(keywords.Length, StringComparer.Ordinal);
        for (var i = 0; i < keywords.Length; i++) dict[keywords[i]] = i;
        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Per Phase 3 Task 18 cycle 7d — build the grid-auto-flow
    /// keyword table. Per CSS Grid L1 §7.7 the grammar is
    /// <c>[ row | column ] || dense</c>; multiple authored forms map
    /// to the same semantic id (e.g., bare <c>dense</c> ≡
    /// <c>row dense</c> ≡ <c>dense row</c>).</summary>
    private static FrozenDictionary<string, int> BuildGridAutoFlowTable()
    {
        // Keywords are whitespace-normalized by KeywordResolver before
        // lookup, so we register the canonical-spaced forms here.
        var dict = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["row"] = 0,
            ["column"] = 1,
            ["row dense"] = 2,
            ["dense row"] = 2,
            ["dense"] = 2,
            ["column dense"] = 3,
            ["dense column"] = 3,
        };
        return dict.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
