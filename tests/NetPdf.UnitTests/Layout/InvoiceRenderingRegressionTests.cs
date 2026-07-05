// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Text.Fonts;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Layout;

/// <summary>
/// Regression tests for two rendering bugs found on real invoice templates (the
/// NetPdf-tester corpus). Both use the deterministic synthetic font so the geometry — and
/// therefore whether a fragment overflows its page — is reproducible without a system font.
/// </summary>
public sealed class InvoiceRenderingRegressionTests
{
    private static HtmlPdfOptions Opts() => new() { FontResolver = new SynthResolver() };

    private static string Page(string body) =>
        "<!DOCTYPE html><html><head><style>@page{size:A4;margin:20mm}</style></head>"
        + "<body style=\"font-family:sans-serif;font-size:12px\">" + body + "</body></html>";

    // ── Bug A: flexbox `justify-content: space-between` with auto-width items ───────────────
    // A `flex-basis: auto` item whose `width` is also auto must size to its CONTENT
    // (max-content per Flexbox §7.2.3), not to zero. Pre-fix it measured as 0, so
    // space-between distributed the entire container width as the gap and the last item
    // overflowed the page — the "Bill to column runs off the right edge" invoice bug.

    [Fact]
    public void Flex_space_between_two_auto_columns_does_not_overflow()
    {
        var result = HtmlPdf.ConvertDetailed(Page(
            "<div style='display:flex;justify-content:space-between'>"
            + "<div><strong>From</strong><br>ACME Corp<br>500 Industrial Way<br>Portland, OR 97201</div>"
            + "<div><strong>Bill to</strong><br>Northwind Traders<br>12 Market Street<br>Seattle, WA 98101</div>"
            + "</div>"), Opts());

        Assert.DoesNotContain(result.Warnings, IsOverflow);
    }

    [Theory]
    [InlineData("<div>A</div><div>B</div>")]                         // 2 items
    [InlineData("<div>A</div><div>B</div><div>C</div>")]             // 3 items
    [InlineData("<div>Left</div><div>Middle</div><div>Right end</div>")]
    public void Flex_space_between_auto_items_stay_within_the_page(string items)
    {
        var result = HtmlPdf.ConvertDetailed(
            Page($"<div style='display:flex;justify-content:space-between'>{items}</div>"), Opts());

        Assert.DoesNotContain(result.Warnings, IsOverflow);
    }

    [Fact]
    public void Flex_space_between_with_explicit_widths_still_works()
    {
        // Control — the explicit-width path was always correct; guard it against the fix.
        var result = HtmlPdf.ConvertDetailed(Page(
            "<div style='display:flex;justify-content:space-between'>"
            + "<div style='width:150px'>From ACME</div><div style='width:150px'>Bill to NW</div></div>"), Opts());

        Assert.DoesNotContain(result.Warnings, IsOverflow);
    }

    private static bool IsOverflow(Diagnostic d) => d.Code == "PDF-CONTENT-OVERFLOW-TRUNCATED-001";

    // ── CSS-wide keyword robustness ────────────────────────────────────────────────────────
    // `initial` / `inherit` / `unset` / … are valid on every property and are cascade-resolved.
    // They still reach per-property resolvers via shorthand expansion (the `background`
    // shorthand resets `background-attachment` to `initial`) or reset stylesheets
    // (`line-height: inherit`), where they used to emit a misleading "not an admitted keyword"
    // warning per element — 21× on a typical invoice. They must NOT warn (and output is unchanged).

    [Theory]
    [InlineData("<div style='background:#eee;width:100px;height:20px'></div>")]   // shorthand → attachment:initial
    [InlineData("<div style='background-attachment:initial;width:100px;height:20px'></div>")]
    [InlineData("<div style='line-height:inherit'>text</div>")]
    [InlineData("<div style='line-height:initial'>text</div>")]
    public void Css_wide_keywords_do_not_emit_an_invalid_value_warning(string body)
    {
        var result = HtmlPdf.ConvertDetailed(Page(body), Opts());

        Assert.DoesNotContain(result.Warnings, d => d.Code == "CSS-PROPERTY-VALUE-INVALID-001");
    }

    private sealed class SynthResolver : IFontResolver
    {
        private static readonly byte[] FontBytes = SyntheticFont.Build();

        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = FontBytes, Family = query.Family });
    }
}
