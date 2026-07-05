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
