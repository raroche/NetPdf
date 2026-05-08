// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Linq;
using NetPdf.Pdf;
using NetPdf.Pdf.Images;
using NetPdf.Text.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Phase2;

/// <summary>
/// Regression tests for Phase C — third wave of pre-launch security
/// hardening covering image decode safety, font decode safety, PDF
/// metadata sanitization, cascade overflow cap, resource fetch budget,
/// and HTTP redirect validation.
/// </summary>
public sealed class PhaseCSecurityHardeningTests
{
    // --- C-1: Image pre-decode validator ------------------------------------

    [Fact]
    public void C1_oversized_input_rejected()
    {
        // 33 MiB > 32 MiB cap.
        var bytes = new byte[33 * 1024 * 1024];
        // Real PNG signature so we don't fail on magic.
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        sig.CopyTo(bytes);
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("32 MiB pre-decode cap", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C1_unknown_magic_rejected()
    {
        var bytes = new byte[64];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("recognized format signature", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C1_png_with_excessive_dimensions_rejected()
    {
        // PNG signature + IHDR with 32768 × 32768 (2× dimension cap).
        var bytes = new byte[64];
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        sig.CopyTo(bytes);
        // IHDR chunk header: 4-byte length, 4-byte type, then 4-byte width, 4-byte height.
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        // Width = 32_768 (0x8000) → bytes 16..19.
        bytes[16] = 0; bytes[17] = 0; bytes[18] = 0x80; bytes[19] = 0;
        // Height = 32_768.
        bytes[20] = 0; bytes[21] = 0; bytes[22] = 0x80; bytes[23] = 0;
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("dimensions", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C1_png_with_pixel_area_overflow_rejected()
    {
        // 16384 × 16384 = MaxPixelArea exactly. 16385 × 16384 is over.
        var bytes = new byte[64];
        ReadOnlySpan<byte> sig = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        sig.CopyTo(bytes);
        bytes[8] = 0; bytes[9] = 0; bytes[10] = 0; bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        // Width = 16385, Height = 16384 — width crosses the per-axis cap so the
        // dimension check fires first. (Pure-area overflow needs both dims at
        // cap which is unrealistic to construct without already over.)
        bytes[16] = 0; bytes[17] = 0; bytes[18] = 0x40; bytes[19] = 0x01;  // 16385
        bytes[20] = 0; bytes[21] = 0; bytes[22] = 0x40; bytes[23] = 0x00;  // 16384
        var result = ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
    }

    [Theory]
    [InlineData(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ImageSafetyValidator.ImageFormat.Png)]
    [InlineData(0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, ImageSafetyValidator.ImageFormat.Jpeg)]
    [InlineData(0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x00, ImageSafetyValidator.ImageFormat.Gif)]
    [InlineData(0x42, 0x4D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, ImageSafetyValidator.ImageFormat.Bmp)]
    public void C1_format_sniffer_recognizes_known_signatures(
        byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7,
        ImageSafetyValidator.ImageFormat expected)
    {
        var bytes = new byte[16];
        bytes[0] = b0; bytes[1] = b1; bytes[2] = b2; bytes[3] = b3;
        bytes[4] = b4; bytes[5] = b5; bytes[6] = b6; bytes[7] = b7;
        Assert.Equal(expected, ImageSafetyValidator.SniffFormat(bytes));
    }

    [Fact]
    public void C1_webp_magic_recognized()
    {
        // RIFF + 4 bytes size + WEBP.
        var bytes = new byte[20];
        bytes[0] = 0x52; bytes[1] = 0x49; bytes[2] = 0x46; bytes[3] = 0x46;
        bytes[8] = 0x57; bytes[9] = 0x45; bytes[10] = 0x42; bytes[11] = 0x50;
        Assert.Equal(ImageSafetyValidator.ImageFormat.WebP, ImageSafetyValidator.SniffFormat(bytes));
    }

    // --- C-2: Font pre-decode validator -------------------------------------

    [Fact]
    public void C2_oversized_font_rejected()
    {
        var bytes = new byte[33 * 1024 * 1024];
        bytes[0] = 0x00; bytes[1] = 0x01; bytes[2] = 0x00; bytes[3] = 0x00; // TTF magic
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("32 MiB", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C2_undersized_font_rejected()
    {
        var bytes = new byte[10]; // < MinBytes = 28
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("minimum", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C2_unknown_format_rejected()
    {
        var bytes = new byte[64]; // all zeros — not a valid sfnt magic
        bytes[0] = 0xAA; bytes[1] = 0xBB; bytes[2] = 0xCC; bytes[3] = 0xDD;
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("recognized format magic", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C2_excessive_table_count_rejected()
    {
        // Valid TTF magic but numTables = 1000 (> MaxTableCount = 64).
        var bytes = new byte[64];
        bytes[0] = 0x00; bytes[1] = 0x01; bytes[2] = 0x00; bytes[3] = 0x00;
        bytes[4] = 0x03; bytes[5] = 0xE8; // numTables = 1000
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("64", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C2_zero_tables_rejected()
    {
        var bytes = new byte[64];
        bytes[0] = 0x00; bytes[1] = 0x01; bytes[2] = 0x00; bytes[3] = 0x00;
        bytes[4] = 0x00; bytes[5] = 0x00; // numTables = 0
        var result = FontSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("0 tables", result.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x00, 0x01, 0x00, 0x00, FontSafetyValidator.FontFormat.TrueType)]
    [InlineData(0x4F, 0x54, 0x54, 0x4F, FontSafetyValidator.FontFormat.OpenTypeCff)]
    [InlineData(0x77, 0x4F, 0x46, 0x46, FontSafetyValidator.FontFormat.Woff)]
    [InlineData(0x77, 0x4F, 0x46, 0x32, FontSafetyValidator.FontFormat.Woff2)]
    [InlineData(0xDE, 0xAD, 0xBE, 0xEF, FontSafetyValidator.FontFormat.Unknown)]
    public void C2_format_sniffer_recognizes_known_signatures(
        byte b0, byte b1, byte b2, byte b3, FontSafetyValidator.FontFormat expected)
    {
        ReadOnlySpan<byte> bytes = stackalloc byte[] { b0, b1, b2, b3 };
        Assert.Equal(expected, FontSafetyValidator.SniffFormat(bytes));
    }

    // --- C-3: PDF metadata sanitization -------------------------------------

    [Fact]
    public void C3_title_strips_control_chars()
    {
        // Build the input with explicit char codes — embeds NUL (0x00),
        // BEL (0x07), ANSI ESC (0x1B), DEL (0x7F).
        var poison = "Hello"
            + (char)0x00
            + (char)0x07
            + (char)0x1B
            + "[31mRed"
            + (char)0x7F
            + "End";
        var sanitized = NetPdf.Pdf.PdfDocument.SanitizeMetadataString(poison);
        // None of the forbidden chars should survive.
        foreach (var c in sanitized)
        {
            Assert.False(c < 0x20 && c != '\t' && c != '\n' && c != '\r',
                $"Sanitized output retained control char U+{(int)c:X4}");
            Assert.NotEqual((char)0x7F, c);
        }
        // Per PR #17 review user-recommendation #3 — forbidden chars now
        // replaced with U+FFFD (REPLACEMENT CHARACTER). EncodeMetadataString
        // routes the non-ASCII output through PdfHexString.
        Assert.Contains("Hello", sanitized, StringComparison.Ordinal);
        Assert.Contains("Red", sanitized, StringComparison.Ordinal);
        Assert.Contains("End", sanitized, StringComparison.Ordinal);
        Assert.Contains('�', sanitized);
    }

    [Fact]
    public void C3_title_capped_at_max_metadata_chars()
    {
        var giant = new string('x', 8 * 1024); // > 4 KiB cap
        var sanitized = NetPdf.Pdf.PdfDocument.SanitizeMetadataString(giant);
        Assert.True(sanitized.Length <= 4096 + 1, // + U+2026 ellipsis
            $"expected ≤ 4096 + 1 chars; got {sanitized.Length}");
        Assert.EndsWith("…", sanitized);
    }

    [Fact]
    public void C3_clean_string_returned_unchanged()
    {
        const string clean = "Invoice 12345 Q4 2026 FY27";
        var sanitized = NetPdf.Pdf.PdfDocument.SanitizeMetadataString(clean);
        Assert.Same(clean, sanitized); // no allocation on clean fast-path
    }

    [Fact]
    public void C3_tab_lf_cr_preserved()
    {
        const string input = "Line1\nLine2\tTabbed\rCarriage";
        var sanitized = NetPdf.Pdf.PdfDocument.SanitizeMetadataString(input);
        Assert.Same(input, sanitized);
    }

    [Fact]
    public void C3_pdf_emission_with_poisoned_title_succeeds()
    {
        // End-to-end: PdfDocument.Save() must not throw on a Title containing
        // control chars (pre-fix the raw chars would have reached
        // PdfLiteralString and... actually pre-fix they wouldn't reach there
        // because Title gets ASCII-validated; but the contract is that the
        // Title field should be cleaned + emit successfully).
        var poison = "Title"
            + (char)0x07
            + (char)0x1B
            + "Body";
        var doc = new PdfDocument { Title = poison };
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();
        Assert.True(bytes.Length > 0);
        // PDF starts with %PDF- header.
        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, 5);
        Assert.Equal("%PDF-", header);
    }

    // --- C-4: Cascade overflow cap ------------------------------------------

    [Fact]
    public void C4_max_matched_declarations_per_render_constant_is_set()
    {
        Assert.Equal(5_000_000, NetPdf.Css.Cascade.CascadeResolver.MaxMatchedDeclarationsPerRender);
    }

    [Fact]
    public void C4_cascade_result_starts_at_zero_count()
    {
        var result = new NetPdf.Css.Cascade.CascadeResult();
        Assert.Equal(0, result.TotalMatchedDeclarationCount);
        Assert.False(result.MatchedLimitReached);
    }

    [Fact]
    public void C4_try_consume_matched_flips_limit_at_overflow()
    {
        var result = new NetPdf.Css.Cascade.CascadeResult();
        result.TryConsumeMatched(NetPdf.Css.Cascade.CascadeResolver.MaxMatchedDeclarationsPerRender + 1);
        Assert.True(result.MatchedLimitReached);
    }

    // --- C-5: Resource fetch budget on SecurityPolicy -----------------------

    [Fact]
    public void C5_safe_default_includes_fetch_budget_caps()
    {
        var policy = SecurityPolicy.SafeDefault;
        Assert.True(policy.MaxResourcesPerRender > 0);
        Assert.True(policy.MaxTotalResourceBytes > 0);
        Assert.True(policy.MaxRedirectHops >= 1);
    }

    [Fact]
    public void C5_fetch_budget_caps_have_sane_defaults()
    {
        var policy = SecurityPolicy.SafeDefault;
        Assert.Equal(200, policy.MaxResourcesPerRender);
        Assert.Equal(100L * 1024 * 1024, policy.MaxTotalResourceBytes);
        Assert.Equal(5, policy.MaxRedirectHops);
    }

    // --- C-6: UriSafetyValidator.ValidateRedirect ---------------------------

    [Fact]
    public void C6_redirect_to_blocked_ip_rejected_via_chained_validation()
    {
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var origin = new Uri("https://example.com/page");
        var redirectTarget = new Uri("https://169.254.169.254/latest/meta-data/");
        var verdict = UriSafetyValidator.ValidateRedirect(origin, redirectTarget, policy, 0);
        Assert.False(verdict.IsSafe);
        Assert.Contains("link-local-or-metadata", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C6_redirect_https_to_http_downgrade_rejected()
    {
        var policy = new SecurityPolicy { AllowHttpScheme = true, AllowHttpsScheme = true };
        var origin = new Uri("https://example.com/page");
        var redirectTarget = new Uri("http://example.com/page");
        var verdict = UriSafetyValidator.ValidateRedirect(origin, redirectTarget, policy, 0);
        Assert.False(verdict.IsSafe);
        Assert.Contains("downgrades https", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C6_redirect_http_to_https_upgrade_allowed()
    {
        var policy = new SecurityPolicy { AllowHttpScheme = true, AllowHttpsScheme = true };
        var origin = new Uri("http://example.com/page");
        var redirectTarget = new Uri("https://example.com/page");
        var verdict = UriSafetyValidator.ValidateRedirect(origin, redirectTarget, policy, 0);
        Assert.True(verdict.IsSafe, verdict.Reason);
    }

    [Fact]
    public void C6_redirect_chain_exceeded_rejected()
    {
        var policy = new SecurityPolicy { AllowHttpsScheme = true, MaxRedirectHops = 3 };
        var origin = new Uri("https://example.com/page");
        var redirectTarget = new Uri("https://example.com/next");
        var verdict = UriSafetyValidator.ValidateRedirect(origin, redirectTarget, policy, hopsAlreadyFollowed: 3);
        Assert.False(verdict.IsSafe);
        Assert.Contains("3-hop cap", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void C6_redirect_under_hop_cap_with_safe_target_allowed()
    {
        var policy = new SecurityPolicy { AllowHttpsScheme = true, MaxRedirectHops = 5 };
        var origin = new Uri("https://example.com/page");
        var redirectTarget = new Uri("https://api.example.com/v1/page");
        var verdict = UriSafetyValidator.ValidateRedirect(origin, redirectTarget, policy, hopsAlreadyFollowed: 2);
        Assert.True(verdict.IsSafe, verdict.Reason);
    }

    // --- PR #17 follow-up: review fixes -------------------------------------

    // P1 #1 — raster fallback now routed through validator + AVIF rejection.
    [Fact]
    public void C1Followup_raster_build_rejects_unknown_bytes()
    {
        var bytes = new byte[64];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        Assert.Throws<InvalidOperationException>(() =>
            NetPdf.Pdf.Images.RasterImageXObject.Build(bytes));
    }

    [Fact]
    public void C1Followup_avif_format_recognized_then_rejected()
    {
        // ISOBMFF box: 4-byte size (any), "ftyp" at 4..7, brand "avif" at 8..11.
        var bytes = new byte[32];
        bytes[0] = 0; bytes[1] = 0; bytes[2] = 0; bytes[3] = 0x18; // box size 24
        bytes[4] = 0x66; bytes[5] = 0x74; bytes[6] = 0x79; bytes[7] = 0x70; // "ftyp"
        bytes[8] = 0x61; bytes[9] = 0x76; bytes[10] = 0x69; bytes[11] = 0x66; // "avif"
        Assert.Equal(NetPdf.Pdf.Images.ImageSafetyValidator.ImageFormat.Avif,
            NetPdf.Pdf.Images.ImageSafetyValidator.SniffFormat(bytes));
        var result = NetPdf.Pdf.Images.ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        Assert.Contains("AVIF", result.Reason!, StringComparison.Ordinal);
    }

    // P1 #2 — image dimension cap tightened to 8 KiB per axis.
    [Fact]
    public void C1Followup_max_dimension_lowered_to_8k()
    {
        Assert.Equal(8 * 1024, NetPdf.Pdf.Images.ImageSafetyValidator.MaxDimension);
    }

    // P1 #3 — non-ASCII metadata routed via UTF-16BE hex string instead of
    // throwing at PdfLiteralString.
    [Fact]
    public void C3Followup_unicode_title_does_not_throw_on_save()
    {
        var doc = new PdfDocument { Title = "Résumé — 2026 中文 🚀" };
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();
        Assert.True(bytes.Length > 0);
        // Output should contain a hex-string Title rendering (starts with "<")
        // for the non-ASCII path, with the UTF-16BE BOM bytes "FEFF".
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Title <FEFF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void C3Followup_ascii_title_still_uses_literal_string()
    {
        var doc = new PdfDocument { Title = "Plain ASCII Title" };
        doc.AddPage(MediaBoxSize.A4);
        var bytes = doc.Save();
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        // ASCII path uses PdfLiteralString form: /Title (...)
        Assert.Contains("/Title (Plain ASCII Title)", text, StringComparison.Ordinal);
    }

    // P2 #6 — ValidateRedirect rejects non-http(s) targets.
    [Theory]
    [InlineData("data:text/plain,hi")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    [InlineData("gopher://example.com/")]
    public void C6Followup_redirect_to_non_http_scheme_rejected(string target)
    {
        var policy = new SecurityPolicy
        {
            AllowHttpScheme = true,
            AllowHttpsScheme = true,
            AllowDataUri = true, // would normally accept data: at top level
            AllowFileScheme = true, // would normally accept file: at top level
        };
        var origin = new Uri("https://example.com/page");
        var verdict = UriSafetyValidator.ValidateRedirect(origin, new Uri(target), policy, 0);
        Assert.False(verdict.IsSafe);
        Assert.Contains("not http(s)", verdict.Reason!, StringComparison.Ordinal);
    }

    // Copilot #1 — relative URIs return Unsafe with clear reason instead of
    // throwing.
    [Fact]
    public void C6Followup_relative_redirect_returns_unsafe()
    {
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var origin = new Uri("https://example.com/page");
        var redirectTarget = new Uri("/next-page", UriKind.Relative);
        var verdict = UriSafetyValidator.ValidateRedirect(origin, redirectTarget, policy, 0);
        Assert.False(verdict.IsSafe);
        Assert.Contains("relative URI", verdict.Reason!, StringComparison.Ordinal);
    }

    // P2 #7 — selector cap pre-compile counts top-level commas.
    [Fact]
    public async Task C2Followup_selector_alternative_cap_runs_pre_compile()
    {
        // 1500-alternative selector. The pre-compile counter should fire +
        // emit CSS-RULE-LIMIT-EXCEEDED-001 with "truncated before
        // compilation" in the message.
        var alternatives = new System.Text.StringBuilder();
        const int count = 1500;
        for (var i = 0; i < count; i++)
        {
            if (i > 0) alternatives.Append(", ");
            alternatives.Append($".x{i}");
        }
        var html = $"<!doctype html><html><head><style>{alternatives} {{ color: red }}</style></head><body><div class='x0'>x</div></body></html>";
        var sink = new CapturingPublicSink();
        using var result = await NetPdf.Phase2.Phase2Pipeline.RunFromHtmlAsync(
            html, new HtmlPdfOptions { Diagnostics = sink });
        Assert.Contains(sink.Diagnostics,
            d => d.Code == "CSS-RULE-LIMIT-EXCEEDED-001"
                 && d.Message.Contains("truncated before compilation"));
    }

    // P2 #8 — BMP int.MinValue dimensions don't throw OverflowException.
    [Fact]
    public void C1Followup_bmp_with_int_min_dimensions_returns_unsafe()
    {
        // BM signature + DIB header with width = 0x80000000 (int.MinValue,
        // little-endian).
        var bytes = new byte[64];
        bytes[0] = 0x42; bytes[1] = 0x4D;
        // Width at 18..21 = 0x80000000.
        bytes[18] = 0x00; bytes[19] = 0x00; bytes[20] = 0x00; bytes[21] = 0x80;
        // Height at 22..25 = 0x80000000.
        bytes[22] = 0x00; bytes[23] = 0x00; bytes[24] = 0x00; bytes[25] = 0x80;
        var result = NetPdf.Pdf.Images.ImageSafetyValidator.Validate(bytes);
        Assert.False(result.IsSafe);
        // Either the Int32-range check or the dimension cap fires; both are valid
        // rejections. Just verify no throw.
    }

    private sealed class CapturingPublicSink : IDiagnosticsSink
    {
        public System.Collections.Generic.List<Diagnostic> Diagnostics { get; } = new();
        public void Emit(Diagnostic d) => Diagnostics.Add(d);
    }
}
