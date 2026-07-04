// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPdf.Pdf.Images;
using NetPdf.Svg;
using NetPdf.Text.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Security;

/// <summary>
/// The consolidated security-regression suite (SEC-2). It pins the engine's defenses against the
/// canonical HTML-to-PDF attack classes as <b>executable assertions</b>, organized by the threat-model
/// taxonomy in <c>docs/security/security-hardening-plan.md</c>:
/// <list type="bullet">
///   <item>V1 SSRF — the URI choke point rejects internal / metadata / private / alt-scheme targets.</item>
///   <item>V2 Local file read — <c>file:</c> is off under the untrusted-HTML profile.</item>
///   <item>V3 XXE — the SVG parser is DTD-prohibited + entity-capped; a malicious entity does not leak.</item>
///   <item>V7 DoS — layered caps degrade hostile input to a diagnostic, never an untyped crash / hang.</item>
///   <item>V8 Malicious PDF output — dangerous URL schemes are stripped; no active-content token escapes.</item>
/// </list>
///
/// <para><b>Extension contract:</b> each subsequent SEC-N hardening task adds its regression tests HERE
/// (a new <c>#region</c> under the matching taxonomy class), so the security surface has one discoverable
/// home. The broad, generative counterpart is the <c>tests/NetPdf.Fuzz</c> smoke harness
/// (<c>docs/security/fuzzing.md</c>) — this suite asserts specific invariants; the fuzzer hunts for the
/// unknown. Companion audits live in <c>Phase2/Phase{A,B,C,D}SecurityHardeningTests</c> (organized by the
/// original audit recommendations rather than by taxonomy).</para>
/// </summary>
[Trait("Category", "Security")]
public sealed class SecurityHardeningTests
{
    // ================================================================================
    // V1 — SSRF: the URI safety choke point
    // ================================================================================

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/", "cloud metadata")]
    [InlineData("https://[fd00::1]/", "IPv6 ULA")]
    [InlineData("https://[::ffff:169.254.169.254]/", "IPv4-mapped metadata")]
    [InlineData("https://10.1.2.3/internal", "private 10/8")]
    [InlineData("https://172.16.0.1/internal", "private 172.16/12")]
    [InlineData("https://192.168.1.1/internal", "private 192.168/16")]
    [InlineData("https://127.0.0.1/", "loopback")]
    [InlineData("https://[::1]/", "IPv6 loopback")]
    [InlineData("https://100.64.0.1/", "CGNAT 100.64/10")]
    public void V1_uri_validator_blocks_internal_targets(string url, string _)
    {
        // AllowHttpsScheme isolates the IP blocklist from the scheme gate: the scheme passes, so an
        // Unsafe verdict must come from the IP check — the SSRF-relevant assertion.
        var policy = new SecurityPolicy { AllowHttpsScheme = true };
        var verdict = UriSafetyValidator.Validate(new Uri(url), policy);
        Assert.False(verdict.IsSafe, $"{url} must be rejected by the SSRF IP blocklist");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://127.0.0.1:6379/_INFO")]
    [InlineData("dict://127.0.0.1:11211/stat")]
    [InlineData("ftp://internal/secret")]
    public void V1_uri_validator_rejects_non_http_schemes_under_untrusted(string url)
    {
        var verdict = UriSafetyValidator.Validate(new Uri(url), SecurityPolicy.UntrustedHtml);
        Assert.False(verdict.IsSafe, $"{url} must be rejected under UntrustedHtml (scheme not allowed)");
    }

    [Fact]
    public async Task V1_untrusted_html_makes_zero_fetches_for_internal_image()
    {
        // The hostile deployment: even WITH an ambient loader, UntrustedHtml must not fetch an
        // internal-IP image (http/https off + IP blocklist). The loader must never be invoked.
        var loader = new CountingLoader();
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = SecurityPolicy.UntrustedHtml,
            ResourceLoader = loader,
        };

        _ = await HtmlPdf.ConvertAsync(
            "<img src=\"http://169.254.169.254/latest/meta-data/\">", options, CancellationToken.None);

        Assert.Equal(0, loader.Count);
    }

    // ================================================================================
    // V1 — SSRF: SEC-3 choke-point invariant for the CSS/font resource surface
    //
    // `@import`, `@font-face src:url()`, and `<link rel=stylesheet>` URLs are EXTRACTED but NOT
    // fetched today (Phase-5 feature). SEC-3 is the sequencing gate: these tests pin that state and
    // prove that WHEN the fetch is wired, the choke point (`SafeResourceLoader`) blocks internal
    // targets + MIME mismatches for the Stylesheet / Font kinds. The architectural guard that every
    // fetch must route through `SafeResourceLoader` lives in `ResourceChokePointInvariantTests`.
    // ================================================================================

    [Theory]
    [InlineData("<style>@import url(\"http://169.254.169.254/x.css\");</style><p>x</p>")]
    [InlineData("<style>@font-face{font-family:x;src:url(\"http://10.0.0.1/f.ttf\")}</style><p style=\"font-family:x\">y</p>")]
    [InlineData("<link rel=\"stylesheet\" href=\"http://192.168.0.1/x.css\"><p>z</p>")]
    public async Task Sec3_untrusted_html_fetches_no_css_or_font_resource(string html)
    {
        var loader = new CountingLoader();
        var options = new HtmlPdfOptions { SecurityPolicy = SecurityPolicy.UntrustedHtml, ResourceLoader = loader };

        _ = await HtmlPdf.ConvertAsync(html, options, CancellationToken.None);

        Assert.Equal(0, loader.Count);
    }

    [Fact]
    public async Task Sec3_css_and_font_are_not_fetched_even_when_http_is_allowed()
    {
        // TRIPWIRE for the Phase-5 CSS/font-loading feature. Even with http/https ALLOWED and a loader
        // present, no @import / @font-face / <link> resource is fetched today (only images are wired).
        // When that feature lands it MUST fetch through SafeResourceLoader.FetchAsync(…, Stylesheet|Font)
        // — at which point this test is updated deliberately, and the SSRF/MIME tests below start guarding
        // the new fetch path. A change that wires CSS/font fetching by any OTHER route trips this.
        var loader = new CountingLoader();
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = new SecurityPolicy { AllowHttpScheme = true, AllowHttpsScheme = true },
            ResourceLoader = loader,
        };
        const string html =
            "<style>@import url(\"http://example.com/a.css\");" +
            "@font-face{font-family:x;src:url(\"http://example.com/f.ttf\")}</style>" +
            "<link rel=\"stylesheet\" href=\"http://example.com/b.css\"><p style=\"font-family:x\">t</p>";

        _ = await HtmlPdf.ConvertAsync(html, options, CancellationToken.None);

        Assert.Equal(0, loader.Count);
    }

    [Theory]
    [InlineData(ResourceKind.Stylesheet)]
    [InlineData(ResourceKind.Font)]
    public async Task Sec3_choke_point_blocks_internal_ip_for_css_and_font_kinds(ResourceKind kind)
    {
        // When CSS/font fetching IS wired through the choke point, an internal-IP target must be blocked
        // by the IP blocklist BEFORE the loader is ever invoked — exactly as it is for images today.
        var inner = new CountingLoader();
        var context = new ResourceFetchContext(
            new SecurityPolicy { AllowHttpsScheme = true }, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner, context);

        var result = await loader.FetchAsync(new Uri("https://169.254.169.254/resource"), kind);

        Assert.False(result.Success);
        Assert.Equal(0, inner.Count);
    }

    [Theory]
    [InlineData(ResourceKind.Stylesheet, "data:text/css,x", "data:text/html,x")]
    [InlineData(ResourceKind.Font, "data:font/ttf,x", "data:text/html,x")]
    public async Task Sec3_choke_point_enforces_kind_specific_mime(ResourceKind kind, string allowedUri, string mismatchUri)
    {
        // The MIME gate keeps a Stylesheet fetch from accepting text/html (and a Font from accepting a
        // stylesheet, etc.). Exercised through the real FetchAsync path via data: URIs (no network).
        var context = new ResourceFetchContext(SecurityPolicy.SafeDefault, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, context); // data: needs no loader

        var allowed = await loader.FetchAsync(new Uri(allowedUri), kind);
        var mismatch = await loader.FetchAsync(new Uri(mismatchUri), kind);

        Assert.True(allowed.Success, $"{allowedUri} should be accepted for {kind}");
        Assert.False(mismatch.Success, $"{mismatchUri} (wrong MIME) must be rejected for {kind}");
    }

    [Theory]
    [InlineData("text/css", ResourceKind.Stylesheet, true)]
    [InlineData("text/html", ResourceKind.Stylesheet, false)]
    [InlineData("font/ttf", ResourceKind.Stylesheet, false)]
    [InlineData("font/woff2", ResourceKind.Font, true)]
    [InlineData("text/html", ResourceKind.Font, false)]
    [InlineData("text/css", ResourceKind.Font, false)]
    [InlineData("image/png", ResourceKind.Image, true)]
    [InlineData("text/html", ResourceKind.Image, false)]
    public void Sec3_kind_mime_allowlist_matrix(string mime, ResourceKind kind, bool allowed)
        => Assert.Equal(allowed, SafeResourceLoader.IsMimeAllowedForKind(mime, kind));

    [Fact]
    public void Sec3_unknown_resource_kind_is_fail_closed()
    {
        // The kind→MIME switch defaults to REJECT, so a ResourceKind added in the future without an
        // explicit allowlist entry can't fetch anything until someone opts it in. Fail-closed by design.
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("text/css", (ResourceKind)999));
        Assert.False(SafeResourceLoader.IsMimeAllowedForKind("application/octet-stream", (ResourceKind)999));
    }

    // --- SEC-6: data: URI MIME hardening ---------------------------------------------

    [Theory]
    [InlineData("data:,hello")]                 // no mediatype
    [InlineData("data:;base64,QUJD")]           // no mediatype, base64
    [InlineData("data:text/html,<b>x</b>")]     // text/html polyglot sneak-path
    [InlineData("data:text/plain,hello")]       // not on any allowlist
    public async Task Sec6_data_uri_without_explicit_allowlisted_mediatype_is_rejected(string uri)
    {
        var context = new ResourceFetchContext(SecurityPolicy.SafeDefault, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, context);

        var result = await loader.FetchAsync(new Uri(uri), ResourceKind.Image);

        Assert.False(result.Success, $"{uri} must be rejected (no explicit allowlisted mediatype)");
    }

    [Fact]
    public async Task Sec6_explicit_image_data_uri_still_loads()
    {
        // Self-contained documents (the vendored-asset pattern) keep working — no false positive.
        var context = new ResourceFetchContext(SecurityPolicy.SafeDefault, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, context);

        var result = await loader.FetchAsync(new Uri("data:image/png;base64,QUJD"), ResourceKind.Image);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Sec6_data_scheme_is_off_under_untrusted_html()
    {
        var context = new ResourceFetchContext(SecurityPolicy.UntrustedHtml, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, context);

        var result = await loader.FetchAsync(new Uri("data:image/png;base64,QUJD"), ResourceKind.Image);

        Assert.False(result.Success);
    }

    // ================================================================================
    // V2 — Local file disclosure
    // ================================================================================

    [Fact]
    public void V2_file_scheme_is_off_under_untrusted_html()
    {
        var verdict = UriSafetyValidator.Validate(new Uri("file:///etc/passwd"), SecurityPolicy.UntrustedHtml);
        Assert.False(verdict.IsSafe);
    }

    [Fact]
    public async Task V2_untrusted_html_makes_zero_fetches_for_file_uri_image()
    {
        var loader = new CountingLoader();
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = SecurityPolicy.UntrustedHtml,
            ResourceLoader = loader,
        };

        _ = await HtmlPdf.ConvertAsync("<img src=\"file:///etc/passwd\">", options, CancellationToken.None);

        Assert.Equal(0, loader.Count);
    }

    // ================================================================================
    // V3 — XXE / entity-expansion DoS (the SVG parse surface)
    // ================================================================================

    [Fact]
    public void V3_svg_external_entity_is_rejected_not_expanded()
    {
        // DtdProcessing.Prohibit rejects the DOCTYPE outright, so the &xxe; entity is never resolved —
        // no file read, no SSRF, no throw. TryRender degrades to null.
        const string xxe =
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>&xxe;</text></svg>";

        var info = SvgRasterizer.TryRender(Encoding.UTF8.GetBytes(xxe), out _);
        Assert.Null(info);
    }

    [Fact]
    public void V3_svg_billion_laughs_does_not_hang_or_throw()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\">");
        sb.Append("<!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">");
        sb.Append("<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">");
        sb.Append("<!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">");
        sb.Append("]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&d;</text></svg>");

        // Must return (not OOM / hang) — the DTD prohibition rejects it before any expansion.
        var info = SvgRasterizer.TryRender(Encoding.UTF8.GetBytes(sb.ToString()), out _);
        Assert.Null(info);
    }

    // --- SEC-8: SVG parser observability ---------------------------------------------

    [Fact]
    public void Sec8_svg_parse_status_distinguishes_blocked_malformed_and_ok()
    {
        var dtd = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaa\">]>" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>&a;</text></svg>");
        NetPdf.Svg.SvgRasterizer.TryRender(dtd, out _, out var dtdStatus);
        Assert.Equal(NetPdf.Svg.SvgParseStatus.Blocked, dtdStatus);

        var malformed = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect");
        NetPdf.Svg.SvgRasterizer.TryRender(malformed, out _, out var malStatus);
        Assert.Equal(NetPdf.Svg.SvgParseStatus.Malformed, malStatus);

        var valid = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\"/></svg>");
        var ok = NetPdf.Svg.SvgRasterizer.TryRender(valid, out _, out var okStatus);
        Assert.Equal(NetPdf.Svg.SvgParseStatus.Ok, okStatus);
        Assert.NotNull(ok);
    }

    [Fact]
    public void Sec8_billion_laughs_svg_image_is_diagnosed_not_silent()
    {
        var svg = "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY a \"aaaaaaaaaa\">" +
            "<!ENTITY b \"&a;&a;&a;&a;&a;\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&b;</text></svg>";
        var dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));

        var result = HtmlPdf.ConvertDetailed($"<img src=\"{dataUri}\">", new HtmlPdfOptions());

        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.SvgParseFailed001);
    }

    // ================================================================================
    // V7 — DoS / resource exhaustion
    // ================================================================================

    [Fact]
    public void V7_pathologically_deep_html_degrades_with_diagnostic_not_untyped_crash()
    {
        // Regression for the gap the SEC-2 fuzz harness found: HTML nested past the layout recursion
        // guard (256) but under the DOM nesting cap (1024) used to escape HtmlPdf.Convert as an untyped
        // InvalidOperationException. It must now degrade to a valid PDF + LAYOUT-RECURSION-DEPTH-EXCEEDED-001.
        var html = new StringBuilder();
        const int depth = 600; // > 256 layout guard, < 1024 DOM cap
        for (var i = 0; i < depth; i++)
        {
            html.Append("<div>");
        }

        html.Append("deep");
        for (var i = 0; i < depth; i++)
        {
            html.Append("</div>");
        }

        var result = HtmlPdf.ConvertDetailed(
            html.ToString(),
            new HtmlPdfOptions { SecurityPolicy = SecurityPolicy.UntrustedHtml });

        Assert.NotEmpty(result.Pdf);
        Assert.True(result.PageCount >= 1);
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.LayoutRecursionDepthExceeded001);
    }

    [Fact]
    public void V7_oversized_image_dimensions_are_rejected_before_decode()
    {
        // PNG signature + IHDR declaring 100000 x 100000 — far past the dimension cap. Rejected on the
        // header peek, before any decoder allocates a pixel buffer.
        var png = new byte[8 + 4 + 4 + 13];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        png[8] = 0x00; png[9] = 0x00; png[10] = 0x00; png[11] = 0x0D;
        png[12] = (byte)'I'; png[13] = (byte)'H'; png[14] = (byte)'D'; png[15] = (byte)'R';
        WriteBe32(png, 16, 100_000);
        WriteBe32(png, 20, 100_000);
        png[24] = 8;  // bit depth
        png[25] = 6;  // RGBA

        Assert.False(ImageSafetyValidator.Validate(png).IsSafe);
    }

    [Fact]
    public void V7_font_with_too_many_tables_is_rejected_before_harfbuzz()
    {
        // sfnt header claiming 0xFFFF tables — over the 64-table cap. Rejected pre-decode.
        var font = new byte[64];
        font[0] = 0x00; font[1] = 0x01; font[2] = 0x00; font[3] = 0x00; // TrueType version
        font[4] = 0xFF; font[5] = 0xFF;                                 // numTables

        Assert.False(FontSafetyValidator.Validate(font).IsSafe);
    }

    [Fact]
    public void V7_validators_never_throw_on_empty_or_garbage_input()
    {
        // The pre-decode validators are total functions — a robustness assertion the fuzzer generalizes.
        Assert.False(ImageSafetyValidator.Validate(ReadOnlySpan<byte>.Empty).IsSafe);
        Assert.False(FontSafetyValidator.Validate(ReadOnlySpan<byte>.Empty).IsSafe);
        Assert.False(ImageSafetyValidator.Validate(new byte[] { 1, 2, 3 }).IsSafe);
        Assert.False(FontSafetyValidator.Validate(new byte[] { 1, 2, 3 }).IsSafe);
    }

    // --- SEC-5: configurable output-byte + page-count caps ---------------------------

    [Fact]
    public void Sec5_page_count_cap_stops_and_diagnoses()
    {
        // ~20 forced page breaks, but the configured cap is 5 → rendering stops at the cap + a diagnostic.
        var html = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            html.Append("<div style=\"page-break-after:always\">p").Append(i).Append("</div>");
        }

        var result = HtmlPdf.ConvertDetailed(
            html.ToString(), new HtmlPdfOptions { SecurityPolicy = new SecurityPolicy { MaxPages = 5 } });

        Assert.True(result.PageCount <= 5, $"expected ≤ 5 pages, got {result.PageCount}");
        Assert.Contains(result.Warnings, d => d.Code == DiagnosticCodes.PdfPageLimitExceeded001);
    }

    [Fact]
    public void Sec5_output_size_cap_aborts_with_typed_exception()
    {
        // Any real PDF is far larger than 100 bytes, so a tiny cap aborts the conversion with a typed code.
        var ex = Assert.Throws<HtmlPdfException>(() => HtmlPdf.Convert(
            "<p>hello</p>", new HtmlPdfOptions { SecurityPolicy = new SecurityPolicy { MaxOutputBytes = 100 } }));

        Assert.Equal(DiagnosticCodes.PdfOutputSizeExceeded001, ex.Code);
    }

    [Fact]
    public void Sec5_default_policy_does_not_trip_the_caps()
    {
        // A normal small multi-break document under the default policy renders cleanly (no false positive).
        var result = HtmlPdf.ConvertDetailed(
            "<div style=\"page-break-after:always\">a</div><div>b</div>", new HtmlPdfOptions());

        Assert.DoesNotContain(result.Warnings, d => d.Code == DiagnosticCodes.PdfPageLimitExceeded001);
        Assert.NotEmpty(result.Pdf);
    }

    [Fact]
    public void Sec5_untrusted_profile_has_tight_output_caps()
    {
        Assert.Equal(500, SecurityPolicy.UntrustedHtml.MaxPages);
        Assert.Equal(50L * 1024 * 1024, SecurityPolicy.UntrustedHtml.MaxOutputBytes);
    }

    // --- SEC-9: explicit CSS-amplification caps --------------------------------------
    //
    // The grid repeat()/track-count caps (TrackRepeat.MaxRepeatCount = 10 000,
    // TrackList.MaxExpandedTrackCount = 50 000) and the counter formatters are already bounded; these
    // tests lock in those guards (regression protection) against the CSS-amplification DoS class.

    [Fact]
    public void Sec9_grid_repeat_over_cap_is_rejected_not_expanded()
    {
        // repeat(1000000,1px) would expand to a million tracks; the cap rejects the declaration (a
        // diagnostic, not a 1M-element allocation) and the conversion completes fast with a valid PDF.
        var sink = new CapturingSink();
        var pdf = HtmlPdf.Convert(
            "<div style=\"display:grid;grid-template-columns:repeat(1000000,1px)\">x</div>",
            new HtmlPdfOptions { Diagnostics = sink });

        Assert.NotEmpty(pdf);
        Assert.Contains(sink.Diagnostics, d => d.Code == "CSS-PROPERTY-VALUE-INVALID-001");
    }

    [Fact]
    public void Sec9_huge_counter_value_does_not_amplify()
    {
        // A pathological counter value formatted as roman must fall back to decimal (CSS Lists L3
        // §7.1.4), NOT emit millions of 'M' characters. Alphabetic/greek are logarithmic in the value.
        var roman = NetPdf.Layout.Boxes.CounterStyleFormatter.TryFormat(2_000_000_000, "upper-roman");
        Assert.NotNull(roman);
        Assert.True(roman!.Length < 20, $"huge roman must fall back to decimal; got {roman.Length} chars");

        var alpha = NetPdf.Layout.Boxes.CounterStyleFormatter.TryFormat(int.MaxValue, "lower-alpha");
        Assert.NotNull(alpha);
        Assert.True(alpha!.Length < 16, $"alphabetic counter is logarithmic; got {alpha.Length} chars");
    }

    // --- SEC-4: DNS-resolution timeout (slow-resolver DoS) ---------------------------
    //
    // getaddrinfo does not reliably honor cancellation on every platform, so an unbounded resolve of a
    // dead / hostile-DNS host could hang the render thread. SafeHttpResourceLoader bounds every resolve
    // at min(ResourceTimeout, 5s). Tested with an injected non-responding resolver.

    [Fact]
    public async Task Sec4_non_responding_dns_resolver_fails_fast_not_hang()
    {
        // A resolver whose task never completes — the timeout, not the resolver, must end the wait.
        var policy = new SecurityPolicy { AllowHttpScheme = true, ResourceTimeout = TimeSpan.FromMilliseconds(150) };
        using var loader = new SafeHttpResourceLoader(
            policy, (_, _) => new System.Threading.Tasks.TaskCompletionSource<System.Net.IPAddress[]>().Task);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(
            () => loader.LoadAsync(new Uri("http://dead-resolver.invalid/x"), ResourceKind.Image, CancellationToken.None).AsTask());
        sw.Stop();

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"DNS resolve should fail fast; took {sw.Elapsed}");
    }

    [Fact]
    public async Task Sec4_dns_timeout_surfaces_as_typed_failure_through_the_choke_point()
    {
        // End-to-end: a hung resolver surfaces as a typed SafeResourceResult failure (not a throw / hang).
        var policy = new SecurityPolicy { AllowHttpScheme = true, ResourceTimeout = TimeSpan.FromMilliseconds(150) };
        using var http = new SafeHttpResourceLoader(
            policy, (_, _) => new System.Threading.Tasks.TaskCompletionSource<System.Net.IPAddress[]>().Task);
        // The wrapper requires the SAME policy instance as its context (policy-divergence guard).
        var context = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(http, context);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await loader.FetchAsync(new Uri("http://dead-resolver.invalid/x"), ResourceKind.Image);
        sw.Stop();

        // A hung resolver must surface as a typed, timeout-related failure — fast, not a hang. Which
        // timeout fires first (the loader's DNS bound vs the wrapper's per-fetch bound, both derived from
        // ResourceTimeout) is a race, so accept either phrasing; the exact DNS message is pinned by the
        // direct test above.
        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        var reason = result.Failure!.Reason;
        Assert.True(
            reason.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("timeout", StringComparison.OrdinalIgnoreCase),
            $"expected a timeout-related failure, got: {reason}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"should fail fast; took {sw.Elapsed}");
    }

    // ================================================================================
    // V8 — Malicious PDF output
    // ================================================================================

    [Fact]
    public void V8_javascript_url_is_stripped_and_absent_from_output()
    {
        // The URL-strip pass runs at HTML parse time, so its diagnostic surfaces through the sink
        // (not the render-time Warnings list). Capture it explicitly.
        var sink = new CapturingSink();
        var pdf = HtmlPdf.Convert(
            "<a href=\"javascript:alert(document.cookie)\">click</a>",
            new HtmlPdfOptions { SecurityPolicy = SecurityPolicy.UntrustedHtml, Diagnostics = sink });

        Assert.Contains(sink.Diagnostics, d => d.Code == "HTML-JAVASCRIPT-URL-IGNORED-001");

        // Facade output streams are uncompressed, so the scheme would be searchable if it leaked.
        var text = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("javascript:", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V8_no_active_content_tokens_in_untrusted_output()
    {
        // Active-content actions must never appear in a rendered PDF — the preflight denylist keeps them
        // out even when the input tries to smuggle the literal tokens through text / attributes.
        var result = HtmlPdf.ConvertDetailed(
            "<div>/OpenAction /JavaScript /Launch /SubmitForm /EmbeddedFile</div>",
            new HtmlPdfOptions { SecurityPolicy = SecurityPolicy.UntrustedHtml });

        var text = Encoding.Latin1.GetString(result.Pdf);
        foreach (var token in new[] { "/OpenAction", "/JavaScript", "/Launch", "/SubmitForm", "/EmbeddedFile" })
        {
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    // --- helpers ---------------------------------------------------------------------

    private static void WriteBe32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    /// <summary>Captures every diagnostic (including parse-time ones) the pipeline emits.</summary>
    private sealed class CapturingSink : IDiagnosticsSink
    {
        public System.Collections.Generic.List<Diagnostic> Diagnostics { get; } = new();

        public void Emit(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);
    }

    /// <summary>A loader that records invocations; used to prove the untrusted profile fetches nothing.</summary>
    private sealed class CountingLoader : IResourceLoader
    {
        public int Count;

        public ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.FromResult(new ResourceResponse
            {
                Content = new byte[10],
                MimeType = "image/png",
            });
        }
    }
}
