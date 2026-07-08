// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// WP-9 — a character the resolved font (and its fallback chain) can't map renders as <c>.notdef</c>
/// ("tofu"). Previously this was SILENT (rule-7 violation). The paint pass now emits
/// <c>FONT-MISSING-GLYPH-001</c> (Info) once per conversion when it draws a visible <c>.notdef</c>
/// box. Driven with <c>SyntheticFont</c> (glyphs only for 'A'/'B'), so any other letter is guaranteed
/// tofu deterministically.
/// </summary>
public sealed class MissingGlyphDiagnosticTests
{
    // Resolves every query to the in-repo SyntheticFont (glyphs for 'A'/'B' only); synchronous.
    private sealed class SyntheticFontResolver : IFontResolver
    {
        public ValueTask<FontFaceData?> ResolveAsync(FontQuery query, CancellationToken ct)
            => new(new FontFaceData { Bytes = SyntheticFont.Build(), Family = query.Family });
    }

    private static PdfRenderResult Render(string text) =>
        HtmlPdf.ConvertDetailed(
            "<!doctype html><html><body><p>" + text + "</p></body></html>",
            new HtmlPdfOptions { FontResolver = new SyntheticFontResolver() });

    [Fact]
    public void Text_fully_covered_by_the_font_emits_no_missing_glyph_diagnostic()
    {
        // 'A' and 'B' are the only glyphs SyntheticFont carries — no tofu, no diagnostic.
        var res = Render("AB BA AB");
        Assert.DoesNotContain(res.Warnings, w => w.Code == "FONT-MISSING-GLYPH-001");
    }

    [Fact]
    public void An_unmapped_character_surfaces_a_missing_glyph_diagnostic()
    {
        // 'C' is not in SyntheticFont → drawn as a visible .notdef box → rule-7 diagnostic (Info).
        var res = Render("ABC");
        var missing = res.Warnings.Where(w => w.Code == "FONT-MISSING-GLYPH-001").ToList();
        Assert.Single(missing);
        Assert.Equal(DiagnosticSeverity.Info, missing[0].Severity);
    }

    [Fact]
    public void Many_unmapped_characters_are_diagnosed_exactly_once()
    {
        // Dedup: a document full of tofu surfaces ONE diagnostic, not one per character.
        var res = Render("CDEFGH IJKL");
        Assert.Equal(1, res.Warnings.Count(w => w.Code == "FONT-MISSING-GLYPH-001"));
    }
}
