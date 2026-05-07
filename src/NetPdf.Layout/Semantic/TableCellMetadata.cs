// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Semantic;

/// <summary>
/// Per Task 15 review Rec 5 — metadata that PDF/UA's <c>/TH</c> + <c>/TD</c>
/// structure types need for accessible table emission per ISO 32000-2 §14.8.5.
/// Captured for <see cref="SemanticKind.TableHeaderCell"/> +
/// <see cref="SemanticKind.TableCell"/> nodes; absent for every other kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source attributes.</b> Each field maps directly to an HTML5 table-cell
/// attribute (per HTML5 §4.9). Defaults are the HTML defaults — <c>RowSpan</c>
/// / <c>ColSpan</c> default to 1; <c>Scope</c> / <c>Headers</c> / <c>Abbr</c>
/// default to <see langword="null"/> (not specified).
/// </para>
/// <para>
/// <b>Computed header associations are out of scope</b> — HTML5's "headers"
/// computation algorithm (§4.9.12) walks the table grid to associate each
/// data cell with its applicable header cells; that's a Phase-3-layout
/// concern (it depends on the resolved row/column grid). Cycle-1 captures
/// only the static <c>headers</c> attribute string for downstream resolution.
/// </para>
/// </remarks>
internal readonly record struct TableCellMetadata
{
    /// <summary>Number of rows the cell spans per the HTML5 <c>rowspan</c>
    /// attribute (§4.9.11). Default 1.</summary>
    public int RowSpan { get; init; }

    /// <summary>Number of columns the cell spans per the HTML5 <c>colspan</c>
    /// attribute (§4.9.10). Default 1.</summary>
    public int ColSpan { get; init; }

    /// <summary>The <c>scope</c> attribute on a header cell — one of
    /// <c>"col"</c>, <c>"row"</c>, <c>"colgroup"</c>, <c>"rowgroup"</c>,
    /// <c>"auto"</c>; <see langword="null"/> when unset. PDF/UA uses this to
    /// emit the <c>/Scope</c> attribute on /TH structure types.</summary>
    public string? Scope { get; init; }

    /// <summary>The <c>headers</c> attribute as authored — a space-separated
    /// list of header-cell IDs the data cell associates with. PDF/UA's
    /// <c>/Headers</c> attribute uses this. <see langword="null"/> when
    /// unset.</summary>
    public string? Headers { get; init; }

    /// <summary>The <c>abbr</c> attribute on a header cell — short form for
    /// the header used in screen readers. PDF/UA emits this as the
    /// <c>/E</c> abbreviation expansion. <see langword="null"/> when
    /// unset.</summary>
    public string? Abbr { get; init; }

    /// <summary>The HTML default — RowSpan=1, ColSpan=1, no scope/headers/
    /// abbr.</summary>
    public static TableCellMetadata Default => new() { RowSpan = 1, ColSpan = 1 };
}
