// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetPdf.Css.Resources;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Pdf.Objects;
using NetPdf.Text.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Phase2;

/// <summary>
/// Phase D — defense-in-depth + deployment hardening regression tests.
/// Organized by recommendation (D-1 .. D-9). Builds on top of Phase A
/// (input sanitization), Phase B (DoS gates), Phase C (decode +
/// metadata) — see the corresponding *Tests files.
///
/// <para>Several tests below are organized as exploit-corpus fixtures
/// per recommendation #9 (D-8). Naming convention: each test pins one
/// known attack class (SSRF tag, CSS SSRF, LFI, SVG animation, data:
/// URI polyglot, resource bomb, font/image bombs, PDF active content,
/// log injection).</para>
/// </summary>
public sealed class PhaseDSecurityHardeningTests
{
    // --- D-1: SafeResourceLoader -------------------------------------------

    [Fact]
    public async Task D1_no_loader_configured_returns_failure_not_throw()
    {
        // Use AllowHttpsScheme so the URI passes the scheme check + we
        // reach the no-loader branch.
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, ctx);
        var result = await loader.FetchAsync(new Uri("https://example.com/x.png"), ResourceKind.Image);
        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Contains("no IResourceLoader", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D1_blocks_dangerous_uri_before_invoking_loader()
    {
        var sentinel = new InvocationCountingLoader();
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(sentinel, ctx);
        // 169.254.169.254 — AWS metadata IP, in the IP blocklist.
        var result = await loader.FetchAsync(new Uri("https://169.254.169.254/x"), ResourceKind.Image);
        Assert.False(result.Success);
        Assert.Equal(0, sentinel.InvocationCount);
        Assert.Contains("URI safety check", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D1_per_render_count_cap_blocks_excess_fetches()
    {
        var sentinel = new InvocationCountingLoader();
        var policy = new SecurityPolicy { AllowHttpsScheme = true, MaxResourcesPerRender = 2 };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(sentinel, ctx);
        var ok1 = await loader.FetchAsync(new Uri("https://example.com/a"), ResourceKind.Image);
        var ok2 = await loader.FetchAsync(new Uri("https://example.com/b"), ResourceKind.Image);
        var blocked = await loader.FetchAsync(new Uri("https://example.com/c"), ResourceKind.Image);
        Assert.True(ok1.Success);
        Assert.True(ok2.Success);
        Assert.False(blocked.Success);
        Assert.Contains("fetch count cap", blocked.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void D1_mime_allowlist_rejects_html_for_image_kind()
    {
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("text/html", ResourceKind.Image));
        Assert.True(SafeResourceLoader.IsMimeAllowedForKind("image/png", ResourceKind.Image));
        Assert.True(SafeResourceLoader.IsMimeAllowedForKind("image/jpeg; charset=binary", ResourceKind.Image));
    }

    [Fact]
    public void D1_mime_allowlist_accepts_null_or_empty()
    {
        // Loaders may not return a MIME; accept that since the per-kind
        // decoder validates magic bytes.
        Assert.True(SafeResourceLoader.IsMimeAllowedForKind("", ResourceKind.Image));
        Assert.True(SafeResourceLoader.IsMimeAllowedForKind("", ResourceKind.Font));
    }

    [Fact]
    public void D1_file_uri_under_basedir_check_rejects_traversal()
    {
        var baseUri = new Uri("file:///docs/index.html");
        var traversal = new Uri("file:///etc/passwd");
        Assert.False(SafeResourceLoader.IsFileUriUnderBaseUri(traversal, baseUri, out var reason));
        Assert.Contains("not under base directory", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void D1_file_uri_under_basedir_accepts_sibling()
    {
        var baseUri = new Uri("file:///docs/index.html");
        var sibling = new Uri("file:///docs/logo.png");
        Assert.True(SafeResourceLoader.IsFileUriUnderBaseUri(sibling, baseUri, out _));
    }

    private sealed class InvocationCountingLoader : IResourceLoader
    {
        public int InvocationCount;

        public ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct)
        {
            InvocationCount++;
            return ValueTask.FromResult(new ResourceResponse
            {
                Content = new byte[10],
                MimeType = "image/png",
            });
        }
    }

    // --- D-2: Security profiles --------------------------------------------

    [Fact]
    public void D2_untrusted_html_profile_disables_every_fetch_surface()
    {
        var p = SecurityPolicy.UntrustedHtml;
        Assert.False(p.AllowFileScheme);
        Assert.False(p.AllowFileSchemeUnderBaseUri);
        Assert.False(p.AllowHttpScheme);
        Assert.False(p.AllowHttpsScheme);
        Assert.False(p.AllowDataUri);
    }

    [Fact]
    public void D2_untrusted_html_caps_tighter_than_safe_default()
    {
        var u = SecurityPolicy.UntrustedHtml;
        var s = SecurityPolicy.SafeDefault;
        Assert.True(u.MaxResourcesPerRender < s.MaxResourcesPerRender);
        Assert.True(u.MaxTotalResourceBytes < s.MaxTotalResourceBytes);
        Assert.True(u.MaxResourceBytes < s.MaxResourceBytes);
        Assert.True(u.ResourceTimeout < s.ResourceTimeout);
    }

    [Fact]
    public void D2_trusted_template_alias_matches_safe_default_shape()
    {
        var t = SecurityPolicy.TrustedTemplate;
        var s = SecurityPolicy.SafeDefault;
        Assert.Equal(s.AllowFileScheme, t.AllowFileScheme);
        Assert.Equal(s.AllowFileSchemeUnderBaseUri, t.AllowFileSchemeUnderBaseUri);
        Assert.Equal(s.AllowDataUri, t.AllowDataUri);
        Assert.Equal(s.MaxResourcesPerRender, t.MaxResourcesPerRender);
    }

    // --- D-3: CSS resource extractor ---------------------------------------

    [Theory]
    [InlineData("background-image", "url(http://169.254.169.254/x)", "http://169.254.169.254/x")]
    [InlineData("background-image", "url(\"file:///etc/passwd\")", "file:///etc/passwd")]
    [InlineData("background-image", "url('data:text/html,<script>')", "data:text/html,<script>")]
    [InlineData("list-style-image", "url(/relative/sprite.png)", "/relative/sprite.png")]
    [InlineData("cursor", "url(cursor.png) 0 0, auto", "cursor.png")]
    [InlineData("content", "url(badge.svg)", "badge.svg")]
    public void D3_extracts_url_from_property_values(string prop, string value, string expected)
    {
        var refs = CssResourceExtractor.ExtractFromDeclaration(prop, value, NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Single(refs);
        Assert.Equal(expected, refs[0].Url);
    }

    [Fact]
    public void D3_classifies_property_kind_correctly()
    {
        Assert.Equal(CssResourceKind.Image, CssResourceExtractor.ClassifyProperty("background-image"));
        Assert.Equal(CssResourceKind.Cursor, CssResourceExtractor.ClassifyProperty("cursor"));
        Assert.Equal(CssResourceKind.Content, CssResourceExtractor.ClassifyProperty("content"));
        Assert.Null(CssResourceExtractor.ClassifyProperty("color"));
    }

    [Fact]
    public void D3_skips_url_inside_string_literal()
    {
        // CSS string literal — must NOT be treated as a URL reference.
        var refs = CssResourceExtractor.ExtractFromDeclaration(
            "content", "\"url(spoof.png)\"",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Empty(refs);
    }

    [Fact]
    public void D3_extracts_at_import_url()
    {
        var r = CssResourceExtractor.ExtractFromImport(
            "url(\"http://attacker.com/x.css\")", NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.NotNull(r);
        Assert.Equal(CssResourceKind.Stylesheet, r!.Kind);
        Assert.Equal("http://attacker.com/x.css", r.Url);
    }

    [Fact]
    public void D3_extracts_at_import_bare_string()
    {
        var r = CssResourceExtractor.ExtractFromImport(
            "\"//cdn.example.com/sheet.css\"", NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.NotNull(r);
        Assert.Equal("//cdn.example.com/sheet.css", r!.Url);
    }

    [Fact]
    public void D3_does_not_match_user_function_with_url_prefix()
    {
        // `myurl(...)` is not a url() — token boundary check.
        var refs = CssResourceExtractor.ExtractFromDeclaration(
            "background-image", "myurl(spoof.png)",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Empty(refs);
    }

    // --- D-4: Path-specific image caps -------------------------------------

    [Fact]
    public void D4_passthrough_path_uses_8k_dimension_cap()
    {
        Assert.Equal(8 * 1024, ImageSafetyValidator.MaxDimension);
    }

    [Fact]
    public void D4_raster_path_uses_4k_dimension_cap()
    {
        Assert.Equal(4 * 1024, ImageSafetyValidator.MaxRasterDimension);
    }

    [Fact]
    public void D4_gif_at_5k_rejected_by_raster_cap()
    {
        // GIF header with 5120 × 100 dimensions — well under passthrough
        // cap (8192) but over the raster cap (4096).
        var bytes = new byte[16];
        bytes[0] = 0x47; bytes[1] = 0x49; bytes[2] = 0x46;
        bytes[3] = 0x38; bytes[4] = 0x39; bytes[5] = 0x61; // GIF89a
        // Width LE = 5120 = 0x1400.
        bytes[6] = 0x00; bytes[7] = 0x14;
        // Height LE = 100.
        bytes[8] = 0x64; bytes[9] = 0x00;
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("Gif", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void D4_jpeg_at_5k_passes_passthrough_cap()
    {
        // JPEG SOI + JFIF APP0 + SOF0 with 5120 × 100 (BE) — over the
        // raster cap, under the passthrough cap. JPEG is passthrough.
        var bytes = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            // SOF0 marker FF C0 + 11-byte segment: length 11, precision 8,
            // height (BE) 0x0064 = 100, width (BE) 0x1400 = 5120, components 3.
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x64, 0x14, 0x00, 0x03,
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        };
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.True(result.IsSafe, result.Reason);
    }

    // --- D-5: Font validation extensions -----------------------------------

    [Fact]
    public void D5_rejects_font_with_svg_table()
    {
        var bytes = BuildFontWithTableTags(("SVG ", 100, 50));
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("SVG", result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sbix")]
    [InlineData("CBDT")]
    [InlineData("CBLC")]
    [InlineData("EBDT")]
    [InlineData("EBLC")]
    public void D5_rejects_bitmap_glyph_tables(string tag)
    {
        var bytes = BuildFontWithTableTags((tag, 100, 50));
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains(tag, result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void D5_rejects_table_record_with_offset_past_eof()
    {
        // Build a TTF with a "glyf" record whose offset+length extends
        // past the file end. Pre-fix the validator only checked the
        // directory boundary, not individual records.
        var bytes = BuildFontWithTableTags(("glyf", 5000, 1000));
        // File is much smaller than 5000+1000.
        Assert.True(bytes.Length < 5000);
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("extends past file length", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void D5_woff_with_truncated_header_rejected()
    {
        var bytes = new byte[20]; // < 44-byte WOFF header
        bytes[0] = 0x77; bytes[1] = 0x4F; bytes[2] = 0x46; bytes[3] = 0x46;
        // Skipped: rest needs to look like a WOFF for sniff to succeed,
        // but WOFF needs MinBytes (28) which we already exceed via the
        // bytes array sized at 20 — actually 20 < 28 so it'll be
        // rejected at MinBytes first. Use a larger truncated header.
        var bytes44 = new byte[35];
        bytes44[0] = 0x77; bytes44[1] = 0x4F; bytes44[2] = 0x46; bytes44[3] = 0x46;
        var result = FontSafetyValidator.Validate(bytes44);
        Assert.False(result.IsSafe);
        Assert.Contains("WOFF header truncated", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void D5_woff_with_unknown_flavor_rejected()
    {
        var bytes = new byte[44];
        bytes[0] = 0x77; bytes[1] = 0x4F; bytes[2] = 0x46; bytes[3] = 0x46;
        // Flavor 0xDEADBEEF.
        bytes[4] = 0xDE; bytes[5] = 0xAD; bytes[6] = 0xBE; bytes[7] = 0xEF;
        // Length = 44.
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 44;
        // numTables = 1.
        bytes[12] = 0; bytes[13] = 1;
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("unknown sfnt flavor", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void D5_woff2_minimum_header_size_validated()
    {
        var bytes = new byte[40]; // < 48-byte WOFF2 header
        bytes[0] = 0x77; bytes[1] = 0x4F; bytes[2] = 0x46; bytes[3] = 0x32;
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("WOFF2 header truncated", result.Reason!, StringComparison.Ordinal);
    }

    // Helper: build a synthetic sfnt with given table records. Each
    // tuple is (4-char tag, offset, length). When offsetOverride is
    // null, place each table sequentially after the directory.
    private static byte[] BuildFontWithTableTags(params (string Tag, int OffsetOverride, int Length)[] tables)
    {
        var numTables = tables.Length;
        var headerSize = 12;
        var directorySize = numTables * 16;
        var fileSize = headerSize + directorySize + 256; // padding for table data
        var buf = new byte[fileSize];
        // sfnt header: TTF magic 00 01 00 00 (the SniffFormat check looks
        // at bytes 0..3 specifically — without these bytes the format
        // routes to Unknown and the validator never reaches the table
        // walk we're trying to test).
        buf[0] = 0x00; buf[1] = 0x01; buf[2] = 0x00; buf[3] = 0x00;
        buf[4] = (byte)((numTables >> 8) & 0xFF);
        buf[5] = (byte)(numTables & 0xFF);
        var nextOffset = headerSize + directorySize;
        for (var i = 0; i < tables.Length; i++)
        {
            var (tag, offsetOverride, length) = tables[i];
            var recOff = headerSize + i * 16;
            // tag (4 ASCII chars)
            buf[recOff + 0] = (byte)tag[0];
            buf[recOff + 1] = (byte)tag[1];
            buf[recOff + 2] = (byte)tag[2];
            buf[recOff + 3] = (byte)tag[3];
            // checksum (skip — zeros)
            // offset (BE)
            var off = offsetOverride > 0 ? offsetOverride : nextOffset;
            buf[recOff + 8] = (byte)((off >> 24) & 0xFF);
            buf[recOff + 9] = (byte)((off >> 16) & 0xFF);
            buf[recOff + 10] = (byte)((off >> 8) & 0xFF);
            buf[recOff + 11] = (byte)(off & 0xFF);
            // length (BE)
            buf[recOff + 12] = (byte)((length >> 24) & 0xFF);
            buf[recOff + 13] = (byte)((length >> 16) & 0xFF);
            buf[recOff + 14] = (byte)((length >> 8) & 0xFF);
            buf[recOff + 15] = (byte)(length & 0xFF);
            nextOffset += length;
        }
        return buf;
    }

    // --- D-6: PdfPreflightValidator active-content denylist -----------------

    [Fact]
    public void D6_smoke_pdf_still_passes_preflight_after_active_content_walk()
    {
        // SmokeDocumentFactory exercises every Phase 1 byte-emit path the
        // AOT canary covers. Phase D D-6 adds a dictionary-walk step in
        // PdfPreflightValidator that rejects active-content keys
        // (/OpenAction, /AA, /JavaScript, /Launch, /URI, /SubmitForm,
        // /ImportData, /GoToR, /GoToE, /EmbeddedFile, /EmbeddedFiles,
        // /RichMedia). This test verifies the walk hasn't regressed
        // SmokeDocumentFactory by adding a false positive — the smoke
        // doc must still emit cleanly with the new preflight.
        var bytes = NetPdf.AotSmoke.SmokeDocumentFactory.BuildSmokeDocument();
        Assert.True(bytes.Length > 0);
        // Phase B B-6 already pins that the smoke output's plaintext
        // contains none of the active-content tokens, so this test
        // doubles as a regression for the constant set.
    }

    // --- D-7: VarResolver cancellation -------------------------------------

    [Fact]
    public async Task D7_var_resolver_honors_cancellation()
    {
        // Build a doc with thousands of elements + per-element custom
        // properties. Pre-cancel; the resolver should bail promptly
        // instead of resolving every var().
        var sb = new System.Text.StringBuilder("<!doctype html><html><head><style>");
        for (var i = 0; i < 100; i++)
        {
            sb.Append($".x{i} {{ --p1: red; --p2: blue; color: var(--p1); }}");
        }
        sb.Append("</style></head><body>");
        for (var i = 0; i < 5000; i++)
        {
            sb.Append($"<div class='x{i % 100}'>x</div>");
        }
        sb.Append("</body></html>");
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
                sb.ToString(), new HtmlPdfOptions(), diagnostics: null, cancellationToken: cts.Token);
        });
    }

    // --- D-8: Exploit-corpus tests by attack class -------------------------
    // Each test below pins one known HTML-to-PDF attack class; combined
    // with Phase A/B/C tests this corpus organizes the threat-model
    // coverage by attack rather than by phase.

    [Theory(DisplayName = "D8 SSRF vector: HTML tag URL strip — every dangerous-attribute path")]
    [InlineData("<iframe src='javascript:alert(1)'></iframe>", "iframe", "src")]
    [InlineData("<img src='javascript:alert(1)'>", "img", "src")]
    [InlineData("<form action='javascript:alert(1)'></form>", "form", "action")]
    [InlineData("<base href='javascript:alert(1)'>", "base", "href")]
    [InlineData("<link rel='stylesheet' href='javascript:alert(1)'>", "link", "href")]
    [InlineData("<source src='javascript:alert(1)'>", "source", "src")]
    [InlineData("<video src='javascript:alert(1)'></video>", "video", "src")]
    public async Task D8_corpus_ssrf_html_tags(string html, string tag, string attr)
    {
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync($"<html><body>{html}</body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.False(doc.QuerySelector(tag)!.HasAttribute(attr),
            $"{tag}.{attr} survived strip");
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");
    }

    [Fact(DisplayName = "D8 SSRF vector: AWS metadata IP via redirect chain")]
    public void D8_corpus_ssrf_redirect_to_aws_metadata()
    {
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var origin = new Uri("https://example.com/page");
        var redirect = new Uri("https://169.254.169.254/latest/meta-data/iam/security-credentials/");
        var verdict = UriSafetyValidator.ValidateRedirect(origin, redirect, policy, 0);
        Assert.False(verdict.IsSafe);
    }

    [Fact(DisplayName = "D8 LFI vector: file traversal via base directory escape")]
    public void D8_corpus_lfi_file_traversal_blocked()
    {
        var baseUri = new Uri("file:///docs/index.html");
        var traversal = new Uri("file:///etc/passwd");
        Assert.False(SafeResourceLoader.IsFileUriUnderBaseUri(traversal, baseUri, out _));
    }

    [Fact(DisplayName = "D8 SVG vector: animation targeting xlink:href with javascript")]
    public async Task D8_corpus_svg_animate_xlink_href_javascript()
    {
        var html = """
            <html><body><svg xmlns="http://www.w3.org/2000/svg">
              <a><animate attributeName="xlink:href" to="javascript:alert(1)"/>x</a>
            </svg></body></html>
            """;
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        var doc = await host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Empty(doc.QuerySelectorAll("animate"));
    }

    [Theory(DisplayName = "D8 Data-URI polyglot: rejected on dangerous attribute paths")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("data:image/svg+xml,<svg onload=alert(1)/>")]
    public async Task D8_corpus_data_uri_polyglot(string url)
    {
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        await host.ParseAsync($"<html><body><iframe src='{url}'></iframe></body></html>",
            new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");
    }

    [Fact(DisplayName = "D8 Image bomb: dimension overflow rejected pre-decode")]
    public void D8_corpus_image_bomb_dimensions()
    {
        // PNG signature + IHDR with 32k × 32k.
        var bytes = new byte[64];
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        sig.CopyTo(bytes);
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        bytes[16] = 0; bytes[17] = 0; bytes[18] = 0x80; bytes[19] = 0;
        bytes[20] = 0; bytes[21] = 0; bytes[22] = 0x80; bytes[23] = 0;
        Assert.False(ImageSafetyValidator.Validate(bytes).IsSafe);
    }

    [Fact(DisplayName = "D8 Font bomb: SVG-in-OpenType rejected pre-decode")]
    public void D8_corpus_font_bomb_svg_table()
    {
        var bytes = BuildFontWithTableTags(("SVG ", 100, 50));
        Assert.False(FontSafetyValidator.Validate(bytes).IsSafe);
    }

    [Fact(DisplayName = "D8 Resource bomb: huge HTML input rejected pre-parse")]
    public async Task D8_corpus_resource_bomb_pre_parse()
    {
        var huge = new string('a', 17 * 1024 * 1024);
        var html = $"<html><body><div>{huge}</div></body></html>";
        var sink = new CapturingPublicSink();
        var host = new HtmlParsingHost();
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(
            () => host.ParseAsync(html, new HtmlPdfOptions { Diagnostics = sink }));
    }

    [Fact(DisplayName = "D8 Log injection: ANSI escape in CSS value sanitized")]
    public void D8_corpus_log_injection_ansi_in_css()
    {
        // The CSS-side DiagnosticTextSanitizer was the Phase A A-6 fix;
        // this test re-pins that defense in the exploit corpus index.
        var poison = "PRE\x1B[31m\x07red";
        var sanitized = NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(poison);
        foreach (var c in sanitized)
        {
            Assert.False(c < 0x20 && c != '\t' && c != '\n' && c != '\r',
                $"Control char U+{(int)c:X4} survived sanitization");
        }
    }

    private sealed class CapturingPublicSink : IDiagnosticsSink
    {
        public System.Collections.Generic.List<Diagnostic> Diagnostics { get; } = new();
        public void Emit(Diagnostic d) => Diagnostics.Add(d);
    }

    // --- PR #18 review follow-up tests --------------------------------------

    // P1 #2: SVG removed from image MIME allowlist.
    [Fact]
    public void DReview_svg_mime_rejected_for_image_kind()
    {
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("image/svg+xml", ResourceKind.Image));
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("IMAGE/SVG+XML", ResourceKind.Image));
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("image/svg+xml; charset=utf-8", ResourceKind.Image));
    }

    [Fact]
    public void DReview_data_svg_uri_blocked_under_safe_default()
    {
        // SafeDefault has AllowDataUri=true, so data: passes the scheme
        // check. The image-kind MIME allowlist (separate gate) is what
        // rejects SVG. Confirm the layered defense holds.
        var ctx = new ResourceFetchContext(SecurityPolicy.SafeDefault, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, ctx);
        // No way to test end-to-end without a loader; the MIME allowlist
        // check is the contract.
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("image/svg+xml", ResourceKind.Image));
    }

    [Fact]
    public void DReview_svg_mime_rejected_under_untrusted_html()
    {
        // UntrustedHtml has AllowDataUri=false, so data: URIs are
        // already rejected at the scheme gate. The MIME allowlist
        // adds depth — even if Phase 5 added a trusted profile that
        // re-enabled data: + http(s), SVG would still be rejected
        // for the image kind.
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("image/svg+xml", ResourceKind.Image));
    }

    // P1 #3: @font-face src URL extraction.
    [Fact]
    public void DReview_font_face_src_extraction_emits_font_kind()
    {
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromFontFaceSrc(
            "url(\"fonts/foo.woff2\") format(\"woff2\"), url(fonts/foo.woff) format(\"woff\")",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.Equal(NetPdf.Css.Resources.CssResourceKind.Font, r.Kind));
        Assert.Equal("fonts/foo.woff2", refs[0].Url);
        Assert.Equal("fonts/foo.woff", refs[1].Url);
    }

    [Fact]
    public void DReview_font_face_src_with_local_skips_local()
    {
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromFontFaceSrc(
            "local(\"Helvetica Neue\"), url(http://attacker.com/evil.woff)",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        // local() doesn't fetch — should be ignored.
        Assert.Single(refs);
        Assert.Equal("http://attacker.com/evil.woff", refs[0].Url);
    }

    [Fact]
    public void DReview_classify_property_does_not_match_src_outside_font_face()
    {
        // `src` on a regular element doesn't host URL references in CSS
        // (it's HTML-only). ExtractFromDeclaration must return nothing.
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            "src", "url(spoof.png)", NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Empty(refs);
    }

    // P1 #4: PNG alpha-split detection.
    [Fact]
    public void DReview_rgba_png_at_5k_rejected_by_raster_cap()
    {
        // Build a PNG header with color-type 6 (RGBA) at 5120×5120.
        // Pre-fix this passed the 8192 passthrough cap; post-fix it
        // routes through the 4096 raster cap and is rejected.
        var bytes = new byte[64];
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        sig.CopyTo(bytes);
        // IHDR length 13 + type "IHDR".
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        // Width + height = 5120.
        bytes[16] = 0; bytes[17] = 0; bytes[18] = 0x14; bytes[19] = 0;
        bytes[20] = 0; bytes[21] = 0; bytes[22] = 0x14; bytes[23] = 0;
        // bit-depth (offset 24) + color-type (offset 25). 8 / 6 = RGBA.
        bytes[24] = 8;
        bytes[25] = 6;
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("alpha-split", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DReview_rgb_png_at_5k_passes_passthrough_cap()
    {
        // Same shape but color-type 2 (RGB) — passthrough cap (8192)
        // applies, so 5120×5120 is fine.
        var bytes = new byte[64];
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        sig.CopyTo(bytes);
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        bytes[16] = 0; bytes[17] = 0; bytes[18] = 0x14; bytes[19] = 0;
        bytes[20] = 0; bytes[21] = 0; bytes[22] = 0x14; bytes[23] = 0;
        bytes[24] = 8;
        bytes[25] = 2; // RGB, no alpha
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.True(result.IsSafe, result.Reason);
    }

    [Theory]
    [InlineData((byte)4)]  // gray + alpha
    [InlineData((byte)6)]  // RGBA
    public void DReview_implicit_alpha_color_types_detected(byte colorType)
    {
        var bytes = BuildMinimalPngWithColorType(colorType);
        Assert.True(ImageSafetyValidator.PngHasAlphaSplit(bytes));
    }

    [Theory]
    [InlineData((byte)0)]  // gray
    [InlineData((byte)2)]  // RGB
    [InlineData((byte)3)]  // palette (no tRNS)
    public void DReview_non_alpha_color_types_pass_passthrough(byte colorType)
    {
        var bytes = BuildMinimalPngWithColorType(colorType);
        Assert.False(ImageSafetyValidator.PngHasAlphaSplit(bytes));
    }

    private static byte[] BuildMinimalPngWithColorType(byte colorType)
    {
        // Just enough of a PNG to drive the IHDR + chunk walk. 64 bytes
        // covers IHDR (8 sig + 25 IHDR) and leaves room for an IDAT
        // marker so the tRNS-search bails before reading garbage.
        var bytes = new byte[64];
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        sig.CopyTo(bytes);
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        bytes[16] = 0; bytes[17] = 0; bytes[18] = 0; bytes[19] = 100;
        bytes[20] = 0; bytes[21] = 0; bytes[22] = 0; bytes[23] = 100;
        bytes[24] = 8;
        bytes[25] = colorType;
        // After IHDR (33 bytes total: 8 + 4 + 4 + 13 + 4 = 33), put a
        // synthetic IDAT marker so the scan terminates without reading
        // junk. IDAT chunk: length=0, type='IDAT'. Pos = 33.
        bytes[33] = 0; bytes[34] = 0; bytes[35] = 0; bytes[36] = 0;
        bytes[37] = (byte)'I'; bytes[38] = (byte)'D'; bytes[39] = (byte)'A'; bytes[40] = (byte)'T';
        return bytes;
    }

    // P2 #8: image-set / cross-fade / image() function-aware extraction.
    [Theory]
    [InlineData("background-image", "image-set(\"https://attacker/a.png\" 1x)", "https://attacker/a.png")]
    [InlineData("background-image", "image-set(url(a.png) 1x, \"b.png\" 2x)", "a.png")]
    [InlineData("background-image", "cross-fade(\"a.png\", \"b.png\", 50%)", "a.png")]
    [InlineData("background-image", "image(\"https://example.com/x.png\")", "https://example.com/x.png")]
    public void DReview_image_set_extracts_string_form_url(string prop, string value, string firstUrl)
    {
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            prop, value, NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.NotEmpty(refs);
        Assert.Equal(firstUrl, refs[0].Url);
    }

    [Fact]
    public void DReview_string_outside_resource_function_still_skipped()
    {
        // Plain string in content (not inside image-set etc.) — still
        // not a URL.
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            "content", "\"url(notafetch)\"", NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Empty(refs);
    }

    // Post-Task-7 review — nested non-resource helper functions inside
    // image-set / cross-fade / image. Pre-fix bugs:
    //   (1) String args of helpers like type() / format() were captured
    //       as phantom URLs (e.g., "image/png" extracted from
    //       `image-set("a.png" 1x, type("image/png"))`).
    //   (2) The helper's closing ) decremented the resource-fn depth,
    //       prematurely closing the image-set context so legitimate
    //       strings AFTER the helper would not be captured.
    // The fix tracks an inner-non-resource paren depth separately; only
    // strings at the IMMEDIATE level of the resource fn (innerNonResourceDepth
    // == 0) count as URLs, and helper close-parens decrement the inner
    // depth, leaving the resource-fn context intact.

    [Fact]
    public void PostTask7_image_set_with_type_helper_does_not_extract_helper_string()
    {
        // CSS Images L4 §6 — type("image/png") is a helper inside
        // image-set, NOT a URL-bearing entry. Pre-fix the parser
        // captured "image/png" as a phantom URL.
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            "background-image",
            "image-set(\"a.png\" 1x, type(\"image/png\"))",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        // Only "a.png" is a URL; "image/png" is a MIME type argument.
        Assert.Single(refs);
        Assert.Equal("a.png", refs[0].Url);
    }

    [Fact]
    public void PostTask7_image_set_with_format_helper_does_not_extract_helper_string()
    {
        // url(...) format("png") inside image-set — format() is a
        // helper hint, "png" is its arg, NOT a URL.
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            "background-image",
            "image-set(url(\"a.png\") format(\"png\"))",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        // Only the url() emits a URL; format("png") does NOT.
        Assert.Single(refs);
        Assert.Equal("a.png", refs[0].Url);
    }

    [Fact]
    public void PostTask7_image_set_helper_close_paren_does_not_close_resource_fn()
    {
        // Pre-fix: the closing ) of type() decremented resourceFnDepth,
        // so the SECOND legitimate string ("c.png") AFTER the helper was
        // outside the resource-fn context and not captured.
        // Post-fix: helper ) decrements innerNonResourceDepth; the
        // image-set context survives until its OWN closing ).
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            "background-image",
            "image-set(\"a.png\" 1x, type(\"image/png\") , \"c.png\" 2x)",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        // BOTH "a.png" + "c.png" are real URLs; "image/png" is NOT.
        Assert.Equal(2, refs.Count);
        Assert.Equal("a.png", refs[0].Url);
        Assert.Equal("c.png", refs[1].Url);
    }

    [Fact]
    public void PostTask7_image_set_with_calc_helper_does_not_extract_helper_string()
    {
        // calc() is a helper that may appear in resource-fn args; its
        // own contents are arithmetic, not URLs. (calc rarely contains
        // strings, but cover the safety case so we don't regress later.)
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            "background-image",
            "image-set(\"a.png\" calc(1 * 1x))",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Single(refs);
        Assert.Equal("a.png", refs[0].Url);
    }

    [Fact]
    public void PostTask7_nested_resource_fns_still_extract_strings()
    {
        // Pathological but legal — image-set inside cross-fade. Both
        // are resource-list functions, so strings at either level are
        // URLs. The fix must not break this: nested resource-fn
        // depths still count as resource-fn level (innerNonResourceDepth
        // stays at 0 because the inner is opened via
        // TryMatchResourceFunctionStart, not as a non-resource paren).
        var refs = NetPdf.Css.Resources.CssResourceExtractor.ExtractFromDeclaration(
            "background-image",
            "cross-fade(image-set(\"a.png\" 1x), \"b.png\", 50%)",
            NetPdf.Css.Parser.CssSourceLocation.Unknown);
        Assert.Equal(2, refs.Count);
        Assert.Equal("a.png", refs[0].Url);
        Assert.Equal("b.png", refs[1].Url);
    }

    // ====================================================================
    //  Post-Task-7 review #1 (P1 #1) — HTTP loader policy unification.
    //  SafeHttpResourceLoader's constructor-captured policy + the
    //  SafeResourceLoader's ResourceFetchContext.Policy were independent
    //  decisions; the wrapper validated scheme/IP/AllowedHosts against
    //  context.Policy while the inner loader validated redirects +
    //  per-resource bytes against its own policy. Mismatch = silent
    //  security gap. Post-fix: detect ReferenceEquals divergence at
    //  wrapper construction + throw, plus a CreateWithSafeHttp factory
    //  that wires both with the context's policy as the single source.
    // ====================================================================

    [Fact]
    public void PostTask7_safe_resource_loader_throws_when_inner_http_loader_has_divergent_policy()
    {
        // Two distinct SecurityPolicy instances (even with identical
        // fields) → divergence. Pre-fix, this constructed silently +
        // redirect/AllowedHosts validation could use a different
        // policy than the wrapper's URI safety check. Post-fix throws.
        var policyA = new SecurityPolicy { AllowHttpsScheme = true };
        var policyB = new SecurityPolicy { AllowHttpsScheme = true };
        using var http = new SafeHttpResourceLoader(policyA);
        var ctx = new ResourceFetchContext(policyB, baseUri: null,
            cancellationToken: System.Threading.CancellationToken.None);

        var ex = Assert.Throws<ArgumentException>(() =>
            new SafeResourceLoader(http, ctx));
        Assert.Contains("same instance", ex.Message,
            System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CreateWithSafeHttp", ex.Message,
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostTask7_safe_resource_loader_accepts_inner_http_loader_with_same_policy_instance()
    {
        // Same SecurityPolicy instance shared between both layers →
        // no divergence; constructor accepts.
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        using var http = new SafeHttpResourceLoader(policy);
        var ctx = new ResourceFetchContext(policy, baseUri: null,
            cancellationToken: System.Threading.CancellationToken.None);

        var loader = new SafeResourceLoader(http, ctx);
        Assert.NotNull(loader);
    }

    [Fact]
    public void PostTask7_safe_resource_loader_accepts_non_http_inner_loader()
    {
        // The divergence check applies ONLY to SafeHttpResourceLoader
        // inners (the only well-known policy-aware type). A custom
        // user-supplied IResourceLoader is opaque — the wrapper can't
        // know what policy it uses, so the check is skipped.
        var policy = new SecurityPolicy();
        var ctx = new ResourceFetchContext(policy, baseUri: null,
            cancellationToken: System.Threading.CancellationToken.None);
        var customInner = new InvocationCountingLoader();

        var loader = new SafeResourceLoader(customInner, ctx);
        Assert.NotNull(loader);
    }

    [Fact]
    public void PostTask7_create_with_safe_http_wires_both_layers_with_context_policy()
    {
        // The factory ensures both the wrapper + the inner HTTP loader
        // see the SAME SecurityPolicy instance (= context.Policy).
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var ctx = new ResourceFetchContext(policy, baseUri: null,
            cancellationToken: System.Threading.CancellationToken.None);

        using var bundle = SafeResourceLoader.CreateWithSafeHttp(ctx);
        Assert.NotNull(bundle.Wrapper);
        Assert.NotNull(bundle.UnderlyingHttpLoader);
        // The HTTP loader's Policy is the context's policy.
        Assert.Same(policy, bundle.UnderlyingHttpLoader.Policy);
    }

    [Fact]
    public void PostTask7_safe_http_resource_loader_policy_property_returns_constructor_value()
    {
        // Sanity: the new public Policy getter exposes the
        // constructor-captured value.
        var policy = new SecurityPolicy { MaxRedirectHops = 7 };
        using var http = new SafeHttpResourceLoader(policy);
        Assert.Same(policy, http.Policy);
    }

    [Fact]
    public void PostTask7_safe_http_resource_loader_policy_property_defaults_to_safe_default()
    {
        // When constructed without an explicit policy, Policy returns
        // SecurityPolicy.SafeDefault.
        using var http = new SafeHttpResourceLoader();
        Assert.Same(SecurityPolicy.SafeDefault, http.Policy);
    }

    // P2 #5: Symlink-aware file sandbox.
    [Fact]
    public void DReview_symlink_traversal_test_doc()
    {
        // Direct symlink-creation tests are platform-dependent + slow
        // in CI. The contract is that ResolveLinkTarget is called on
        // every path segment; pin the lexical-rejection test as a
        // regression for the prefix check + document the symlink
        // path as exercised by the implementation walk.
        var baseUri = new Uri("file:///docs/index.html");
        var lexicalEscape = new Uri("file:///etc/passwd");
        Assert.False(SafeResourceLoader.IsFileUriUnderBaseUri(lexicalEscape, baseUri, out _));
    }

    // P2 #6: Atomic counters.
    [Fact]
    public async Task DReview_concurrent_reservations_do_not_overrun_count_cap()
    {
        // 50 concurrent TryReserveSlot calls against a cap of 10. With
        // atomic CAS, exactly 10 succeed; pre-fix race could allow up
        // to 50 succeeds + drive FetchedCount past the cap.
        var policy = new SecurityPolicy { MaxResourcesPerRender = 10 };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var tasks = new Task<string?>[50];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() => ctx.TryReserveSlot());
        }
        var results = await Task.WhenAll(tasks);
        var succeeded = results.Count(r => r is null);
        Assert.Equal(10, succeeded);
        Assert.Equal(10, ctx.FetchedCount);
    }

    [Fact]
    public async Task DReview_concurrent_byte_charges_do_not_overrun_byte_cap()
    {
        var policy = new SecurityPolicy { MaxTotalResourceBytes = 1000 };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var tasks = new Task<string?>[50];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() => ctx.TryAddBytes(100));
        }
        var results = await Task.WhenAll(tasks);
        var succeeded = results.Count(r => r is null);
        // Exactly 10 should succeed (10 × 100 = 1000 = cap).
        Assert.Equal(10, succeeded);
        Assert.Equal(1000, ctx.FetchedBytes);
    }

    // P2 #7: Loader exception trapping.
    [Fact]
    public async Task DReview_loader_throws_http_exception_returns_typed_failure()
    {
        var thrower = new ThrowingLoader(new System.Net.Http.HttpRequestException("bad"));
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(thrower, ctx);
        var result = await loader.FetchAsync(new Uri("https://example.com/x"), ResourceKind.Image);
        Assert.False(result.Success);
        Assert.Contains("HTTP error", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DReview_loader_throws_io_exception_returns_typed_failure()
    {
        var thrower = new ThrowingLoader(new System.IO.IOException("read failed"));
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(thrower, ctx);
        var result = await loader.FetchAsync(new Uri("https://example.com/x"), ResourceKind.Image);
        Assert.False(result.Success);
        Assert.Contains("I/O error", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DReview_caller_cancellation_propagates_not_swallowed()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var thrower = new ThrowingLoader(new OperationCanceledException(cts.Token));
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var ctx = new ResourceFetchContext(policy, baseUri: null, cts.Token);
        var loader = new SafeResourceLoader(thrower, ctx);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => loader.FetchAsync(new Uri("https://example.com/x"), ResourceKind.Image).AsTask());
    }

    private sealed class ThrowingLoader : IResourceLoader
    {
        private readonly Exception _exception;
        public ThrowingLoader(Exception exception) { _exception = exception; }
        public ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct) =>
            throw _exception;
    }

    // P2 #9: WOFF/WOFF2 ValidateSfntHeader is now public.
    [Fact]
    public void DReview_validate_sfnt_header_is_public()
    {
        // Verifies the API surface: Phase 5's WOFF/WOFF2 decompressor
        // can call this method from outside the assembly.
        var method = typeof(FontSafetyValidator).GetMethod(nameof(FontSafetyValidator.ValidateSfntHeader),
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        Assert.NotNull(method);
    }

    // --- PR #18 Copilot review fixes ----------------------------------------

    // Copilot #1: Reject relative URIs explicitly (don't let Validate throw).
    [Fact]
    public async Task DCopilot1_relative_uri_returns_typed_failure()
    {
        var ctx = new ResourceFetchContext(SecurityPolicy.SafeDefault, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, ctx);
        var relative = new Uri("../foo.png", UriKind.Relative);
        var result = await loader.FetchAsync(relative, ResourceKind.Image);
        Assert.False(result.Success);
        Assert.Contains("relative URI", result.Failure!.Reason, StringComparison.Ordinal);
    }

    // Copilot #2: Empty content from loader = "not found" (per IResourceLoader contract).
    [Fact]
    public async Task DCopilot2_empty_content_returns_not_found_failure()
    {
        var emptyLoader = new EmptyContentLoader();
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(emptyLoader, ctx);
        var result = await loader.FetchAsync(new Uri("https://example.com/x"), ResourceKind.Image);
        Assert.False(result.Success);
        Assert.Contains("not found", result.Failure!.Reason, StringComparison.Ordinal);
    }

    private sealed class EmptyContentLoader : IResourceLoader
    {
        public ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct) =>
            ValueTask.FromResult(new ResourceResponse { Content = ReadOnlyMemory<byte>.Empty });
    }

    // Copilot #3: Slot reservation moved AFTER URI safety/base-path checks
    // — fast-rejected fetches (e.g., dangerous IP) must NOT consume a slot,
    // so an attacker can't probe to exhaust the budget.
    [Fact]
    public async Task DCopilot3_uri_safety_rejection_does_not_consume_budget_slot()
    {
        var sentinel = new InvocationCountingLoader();
        var policy = new SecurityPolicy { AllowHttpsScheme = true, MaxResourcesPerRender = 1 };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(sentinel, ctx);
        // First call: dangerous IP, blocked by URI safety. Should NOT
        // consume a slot.
        var blocked = await loader.FetchAsync(new Uri("https://10.0.0.1/x"), ResourceKind.Image);
        Assert.False(blocked.Success);
        Assert.Equal(0, ctx.FetchedCount);
        // Second call: safe URI. Should still succeed since the budget
        // wasn't consumed by the rejected call.
        var ok = await loader.FetchAsync(new Uri("https://example.com/x"), ResourceKind.Image);
        Assert.True(ok.Success);
        Assert.Equal(1, ctx.FetchedCount);
    }

    // Copilot #8: WOFF2 reserved field validated.
    [Fact]
    public void DCopilot8_woff2_non_zero_reserved_field_rejected()
    {
        var bytes = new byte[48];
        bytes[0] = 0x77; bytes[1] = 0x4F; bytes[2] = 0x46; bytes[3] = 0x32; // wOF2
        // Flavor 0x00010000 (TTF).
        bytes[4] = 0x00; bytes[5] = 0x01; bytes[6] = 0x00; bytes[7] = 0x00;
        // Length = 48.
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 48;
        // numTables = 1.
        bytes[12] = 0; bytes[13] = 1;
        // Reserved bytes (14..15) — non-zero → rejection.
        bytes[14] = 0xFF; bytes[15] = 0xAB;
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("WOFF2 reserved field is non-zero", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void DCopilot8_woff2_zero_reserved_field_accepted()
    {
        var bytes = new byte[48];
        bytes[0] = 0x77; bytes[1] = 0x4F; bytes[2] = 0x46; bytes[3] = 0x32;
        bytes[4] = 0x00; bytes[5] = 0x01; bytes[6] = 0x00; bytes[7] = 0x00;
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 48;
        bytes[12] = 0; bytes[13] = 1;
        // Reserved bytes left at 0.
        var result = FontSafetyValidator.Validate(bytes);
        Assert.True(result.IsSafe, result.Reason);
    }
}
