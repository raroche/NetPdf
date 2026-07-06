// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Document metadata — the HTML <c>&lt;title&gt;</c> / <c>&lt;meta&gt;</c> descriptors + <c>&lt;html
/// lang&gt;</c> flow into the PDF <c>/Info</c> dictionary, the catalog <c>/Lang</c> +
/// <c>/ViewerPreferences</c>, and the XMP <c>/Metadata</c> stream; <see cref="HtmlPdfOptions"/> overrides
/// the harvested values and can add custom <c>/Info</c> entries. Facade output is uncompressed, so every
/// dictionary is string-inspectable.
/// </summary>
public sealed class DocumentMetadataTests
{
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    // ── Task 1: harvest metadata from the HTML head ────────────────────────────────────────

    [Fact]
    public void Title_and_meta_descriptors_flow_into_the_info_dictionary()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><title>Q3 Invoice</title>" +
            "<meta name=\"author\" content=\"ACME Billing\">" +
            "<meta name=\"description\" content=\"Quarterly statement\">" +
            "<meta name=\"keywords\" content=\"invoice, acme, q3\"></head>" +
            "<body><p>x</p></body></html>"));

        Assert.Contains("/Title (Q3 Invoice)", pdf);
        Assert.Contains("/Author (ACME Billing)", pdf);
        Assert.Contains("/Subject (Quarterly statement)", pdf);
        Assert.Contains("/Keywords (invoice, acme, q3)", pdf);
        Assert.Contains("/Producer (NetPdf)", pdf);
    }

    [Fact]
    public void Meta_name_matching_is_case_insensitive()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><meta name=\"AUTHOR\" content=\"Jane\"></head>" +
            "<body>x</body></html>"));
        Assert.Contains("/Author (Jane)", pdf);
    }

    [Fact]
    public void A_meta_in_the_body_is_content_not_document_metadata()
    {
        // Only <head> descriptors count — a body <meta name="author"> is page content.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><title>T</title></head>" +
            "<body><meta name=\"author\" content=\"Not The Author\"><p>x</p></body></html>"));
        Assert.DoesNotContain("/Author", pdf);
        Assert.DoesNotContain("Not The Author", pdf);
    }

    // ── Task 1 precedence: explicit options win over the HTML ───────────────────────────────

    [Fact]
    public void Options_override_the_html_harvested_metadata()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><title>From HTML</title>" +
            "<meta name=\"author\" content=\"HTML Author\"></head><body>x</body></html>",
            new HtmlPdfOptions { Title = "From Options", Author = "Options Author" }));

        Assert.Contains("/Title (From Options)", pdf);
        Assert.Contains("/Author (Options Author)", pdf);
        Assert.DoesNotContain("From HTML", pdf);
    }

    [Fact]
    public void Options_subject_keywords_creator_are_emitted()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>x</body></html>",
            new HtmlPdfOptions { Subject = "Statement", Keywords = "a, b", Creator = "Billing App" }));

        Assert.Contains("/Subject (Statement)", pdf);
        Assert.Contains("/Keywords (a, b)", pdf);
        Assert.Contains("/Creator (Billing App)", pdf);
    }

    // ── Task 2: catalog /Lang + /ViewerPreferences ──────────────────────────────────────────

    [Fact]
    public void Html_lang_becomes_the_catalog_lang()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html lang=\"de\"><head><title>Rechnung</title></head><body>x</body></html>"));
        Assert.Contains("/Lang (de)", pdf);
    }

    [Fact]
    public void Html_lang_wins_over_the_options_language_fallback()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html lang=\"fr\"><body>x</body></html>",
            new HtmlPdfOptions { Language = "en" }));
        Assert.Contains("/Lang (fr)", pdf);
    }

    [Fact]
    public void A_title_emits_viewer_preferences_display_doc_title()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><title>Named</title></head><body>x</body></html>"));
        Assert.Contains("/ViewerPreferences", pdf);
        Assert.Contains("/DisplayDocTitle true", pdf);
    }

    // ── Task 3: XMP /Metadata stream ────────────────────────────────────────────────────────

    [Fact]
    public void Xmp_metadata_stream_mirrors_the_title_and_author()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html lang=\"en\"><head><title>XMP Doc</title>" +
            "<meta name=\"author\" content=\"Writer\"></head><body>x</body></html>"));

        Assert.Contains("/Type /Metadata", pdf);
        Assert.Contains("/Subtype /XML", pdf);
        Assert.Contains("<?xpacket begin=", pdf);
        Assert.Contains("<dc:title>", pdf);
        Assert.Contains("XMP Doc", pdf);
        Assert.Contains("<dc:creator>", pdf);
        Assert.Contains("Writer", pdf);
    }

    [Fact]
    public void Xmp_escapes_xml_special_characters()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>x</body></html>",
            new HtmlPdfOptions { Title = "A & B <tag>" }));
        Assert.Contains("A &amp; B &lt;tag&gt;", pdf);
    }

    // ── Task 4: custom /Info entries ────────────────────────────────────────────────────────

    [Fact]
    public void Custom_document_properties_become_extra_info_keys()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>x</body></html>",
            new HtmlPdfOptions
            {
                DocumentProperties = new Dictionary<string, string>
                {
                    ["InvoiceNumber"] = "INV-42",
                    ["Department"] = "Finance",
                },
            }));

        Assert.Contains("/InvoiceNumber (INV-42)", pdf);
        Assert.Contains("/Department (Finance)", pdf);
    }

    [Fact]
    public void Custom_properties_cannot_override_reserved_keys()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><title>Real Title</title></head><body>x</body></html>",
            new HtmlPdfOptions
            {
                DocumentProperties = new Dictionary<string, string> { ["Title"] = "Hijacked" },
            }));

        Assert.Contains("/Title (Real Title)", pdf);
        Assert.DoesNotContain("Hijacked", pdf);
    }

    // ── Task 5: opt-in creation / modification timestamps ───────────────────────────────────

    [Fact]
    public void Creation_and_mod_dates_are_emitted_in_pdf_date_format_when_set()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>x</body></html>",
            new HtmlPdfOptions
            {
                CreationDate = new System.DateTimeOffset(2026, 7, 5, 9, 30, 0, System.TimeSpan.Zero),
                ModDate = new System.DateTimeOffset(2026, 7, 5, 10, 0, 0, System.TimeSpan.Zero),
            }));

        // ISO 32000 §7.9.4 date string: D:YYYYMMDDHHmmSS±HH'mm'.
        Assert.Contains("/CreationDate (D:20260705093000", pdf);
        Assert.Contains("/ModDate (D:20260705100000", pdf);
    }

    [Fact]
    public void No_dates_are_emitted_by_default_for_determinism()
    {
        var pdf = Latin1(HtmlPdf.Convert("<!DOCTYPE html><html><head><title>T</title></head><body>x</body></html>"));
        Assert.DoesNotContain("/CreationDate", pdf);
        Assert.DoesNotContain("/ModDate", pdf);
    }

    // ── Review [P2]: XMP shares the /Info sanitize + length cap ─────────────────────────────

    [Fact]
    public void Oversized_metadata_is_capped_in_both_info_and_xmp()
    {
        // A hostile multi-KB <title> must not inflate the XMP stream any more than /Info: both go
        // through the same sanitize + MaxMetadataChars (~4096) cap. If XMP were unbounded, a run of
        // the title char near the raw 10k length would appear.
        var huge = new string('A', 10_000);
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>x</body></html>", new HtmlPdfOptions { Title = huge }));

        var maxRun = MaxConsecutive(pdf, 'A');
        Assert.True(maxRun is > 4000 and < 4200, $"title not capped as expected: longest A-run={maxRun}");
    }

    [Fact]
    public void Control_characters_are_scrubbed_from_the_xmp_packet()
    {
        // Control chars (0x01/0x07) in author-controlled metadata must never reach the XMP packet raw —
        // the shared sanitizer replaces them with U+FFFD before the builder XML-escapes. Isolate the
        // xpacket region so binary font bytes elsewhere in the PDF don't confuse the assertion.
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>x</body></html>",
            new HtmlPdfOptions { Title = "CleanTitleEnd" }));

        var start = pdf.IndexOf("<?xpacket begin", System.StringComparison.Ordinal);
        var end = pdf.IndexOf("<?xpacket end", System.StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "no XMP packet found");
        var packet = pdf.Substring(start, end - start);
        foreach (var c in packet)
            Assert.False(c < 0x20 && c is not ('\t' or '\r' or '\n'), $"raw control char U+{(int)c:X4} in XMP");
        Assert.Contains("CleanTitleEnd".Substring(0, 5), packet);   // the clean text survives
    }

    private static int MaxConsecutive(string s, char c)
    {
        int max = 0, run = 0;
        foreach (var ch in s)
        {
            run = ch == c ? run + 1 : 0;
            if (run > max) max = run;
        }

        return max;
    }

    // ── Review [P3]: creation / modification dates in XMP (not just /Info) ───────────────────

    [Fact]
    public void Dates_are_mirrored_in_the_xmp_stream()
    {
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><head><title>Dated</title></head><body>x</body></html>",
            new HtmlPdfOptions
            {
                CreationDate = new System.DateTimeOffset(2026, 7, 5, 9, 30, 0, System.TimeSpan.Zero),
                ModDate = new System.DateTimeOffset(2026, 7, 5, 10, 0, 0, System.TimeSpan.Zero),
            }));

        Assert.Contains("<xmp:CreateDate>2026-07-05T09:30:00+00:00</xmp:CreateDate>", pdf);
        Assert.Contains("<xmp:ModifyDate>2026-07-05T10:00:00+00:00</xmp:ModifyDate>", pdf);
    }

    [Fact]
    public void A_date_only_document_still_emits_an_xmp_stream()
    {
        // Even with no descriptive metadata, a set date warrants an XMP packet (mirrors /Info).
        var pdf = Latin1(HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>x</body></html>",
            new HtmlPdfOptions { CreationDate = new System.DateTimeOffset(2026, 1, 2, 3, 4, 5, System.TimeSpan.Zero) }));
        Assert.Contains("/Type /Metadata", pdf);
        Assert.Contains("<xmp:CreateDate>2026-01-02T03:04:05+00:00</xmp:CreateDate>", pdf);
    }

    // ── Review [P3]: custom /Info entries emit in deterministic (ordinal) key order ──────────

    [Fact]
    public void Custom_properties_emit_in_ordinal_key_order_regardless_of_insertion_order()
    {
        static byte[] Build(params (string k, string v)[] entries)
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in entries) d[k] = v;
            return HtmlPdf.Convert("<!DOCTYPE html><html><body>x</body></html>",
                new HtmlPdfOptions { DocumentProperties = d });
        }

        // Two different insertion orders of the same entries must produce byte-identical output.
        var a = Build(("Zebra", "1"), ("Alpha", "2"), ("Mango", "3"));
        var b = Build(("Mango", "3"), ("Zebra", "1"), ("Alpha", "2"));
        Assert.Equal(a, b);

        // And the emitted order is ordinal: Alpha before Mango before Zebra.
        var pdf = Latin1(a);
        var iAlpha = pdf.IndexOf("/Alpha", System.StringComparison.Ordinal);
        var iMango = pdf.IndexOf("/Mango", System.StringComparison.Ordinal);
        var iZebra = pdf.IndexOf("/Zebra", System.StringComparison.Ordinal);
        Assert.True(iAlpha >= 0 && iAlpha < iMango && iMango < iZebra, "custom keys not in ordinal order");
    }

    // ── Byte-stability guard: a bare document emits none of the new catalog entries ─────────

    [Fact]
    public void A_document_without_metadata_emits_no_lang_metadata_or_viewer_prefs()
    {
        var pdf = Latin1(HtmlPdf.Convert("<!DOCTYPE html><html><body><div>plain</div></body></html>"));

        Assert.DoesNotContain("/Lang", pdf);
        Assert.DoesNotContain("/ViewerPreferences", pdf);
        Assert.DoesNotContain("/Type /Metadata", pdf);
        Assert.DoesNotContain("<?xpacket", pdf);
        // The Info dictionary still always carries the Producer.
        Assert.Contains("/Producer (NetPdf)", pdf);
    }
}
