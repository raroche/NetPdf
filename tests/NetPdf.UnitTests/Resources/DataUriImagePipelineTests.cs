// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading;
using System.Threading.Tasks;
using NetPdf.UnitTests.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Resources;

/// <summary>
/// Img-pipeline cycle — the <c>data:</c> URI decode path (<see cref="DataUriParser"/>) and its
/// <see cref="SafeResourceLoader"/> integration: a data: URI needs NO user loader (the
/// self-contained default), still flows through the policy + budget defenses, and respects the
/// <see cref="SecurityPolicy.AllowDataUri"/> switch.
/// </summary>
public sealed class DataUriImagePipelineTests
{
    [Fact]
    public void Base64_data_uri_decodes_payload_and_mime()
    {
        var png = SyntheticPng.BuildOpaqueRgb8(4, 4);
        var uri = new Uri("data:image/png;base64," + Convert.ToBase64String(png));
        Assert.True(DataUriParser.TryDecode(uri, maxBytes: 1 << 20, out var bytes, out var mime, out _));
        Assert.Equal(png, bytes);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void Percent_encoded_textual_data_uri_decodes()
    {
        var uri = new Uri("data:text/plain,hello%20world");
        Assert.True(DataUriParser.TryDecode(uri, maxBytes: 1 << 20, out var bytes, out var mime, out _));
        Assert.Equal("hello world", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal("text/plain", mime);
    }

    [Fact]
    public void Malformed_data_uris_are_rejected_with_reasons()
    {
        Assert.False(DataUriParser.TryDecode(
            new Uri("data:image/png;base64"), 1 << 20, out _, out _, out var noComma));
        Assert.Contains("no comma", noComma, StringComparison.Ordinal);

        Assert.False(DataUriParser.TryDecode(
            new Uri("data:image/png;base64,!!!notbase64!!!"), 1 << 20, out _, out _, out var badB64));
        Assert.Contains("base64", badB64, StringComparison.Ordinal);

        // The ENCODED length already exceeds the cap → rejected pre-decode.
        var big = new string('A', 4096);
        Assert.False(DataUriParser.TryDecode(
            new Uri("data:image/png;base64," + big), maxBytes: 16, out _, out _, out var tooBig));
        Assert.Contains("cap", tooBig, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_uri_fetch_succeeds_with_no_loader_configured()
    {
        // The self-contained default: SafeDefault allows data:, no IResourceLoader needed.
        var png = SyntheticPng.BuildOpaqueRgb8(4, 4);
        var ctx = new ResourceFetchContext(SecurityPolicy.SafeDefault, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, ctx);
        var result = await loader.FetchAsync(
            new Uri("data:image/png;base64," + Convert.ToBase64String(png)), ResourceKind.Image);
        Assert.True(result.Success);
        Assert.Equal(png, result.Response.Content.ToArray());
        Assert.Equal("image/png", result.Response.MimeType);
    }

    [Fact]
    public async Task Data_uri_fetch_respects_AllowDataUri_off()
    {
        var policy = new SecurityPolicy { AllowDataUri = false };
        var ctx = new ResourceFetchContext(policy, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, ctx);
        var result = await loader.FetchAsync(
            new Uri("data:image/png;base64,AAAA"), ResourceKind.Image);
        Assert.False(result.Success);
        Assert.Contains("data: URIs are disabled", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Data_uri_fetch_rejects_mime_outside_the_kind_allowlist()
    {
        // text/html declared as an Image resource — the polyglot defense applies to inline
        // payloads exactly like fetched ones.
        var ctx = new ResourceFetchContext(SecurityPolicy.SafeDefault, baseUri: null, CancellationToken.None);
        var loader = new SafeResourceLoader(inner: null, ctx);
        var result = await loader.FetchAsync(
            new Uri("data:text/html,<b>x</b>"), ResourceKind.Image);
        Assert.False(result.Success);
        Assert.Contains("not in allowlist", result.Failure!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_percent_escapes_fail_without_throwing()
    {
        // PR #166 review P1 — the no-throw resource contract: a malformed percent escape in an
        // attacker-controlled payload returns false with a reason (the pre-fix
        // Uri.UnescapeDataString path could let a BCL exception escape FetchAsync). Both the
        // base64 and non-base64 branches percent-decode through the same raw decoder.
        Assert.False(DataUriParser.TryDecode(
            new Uri("data:image/png;base64,AA%G1AA"), 1 << 20, out _, out _, out var r1));
        Assert.Contains("percent escape", r1, StringComparison.Ordinal);

        Assert.False(DataUriParser.TryDecode(
            new Uri("data:text/plain,abc%4"), 1 << 20, out _, out _, out var r2));
        Assert.Contains("percent escape", r2, StringComparison.Ordinal);
    }

    [Fact]
    public void Percent_encoded_binary_non_base64_payload_round_trips()
    {
        // PR #166 review P3 — RFC 2397 octet semantics: %XX decodes to the raw byte, so a
        // percent-encoded BINARY payload (bytes ≥ 0x80) survives exactly (the pre-fix
        // unescape-to-string + UTF-8 re-encode corrupted it).
        var png = SyntheticPng.BuildOpaqueRgb8(4, 4);   // starts 0x89 'P' 'N' 'G' — binary
        var sb = new System.Text.StringBuilder("data:image/png,");
        foreach (var b in png) sb.Append('%').Append(b.ToString("X2"));
        Assert.True(DataUriParser.TryDecode(
            new Uri(sb.ToString()), 1 << 20, out var bytes, out var mime, out _));
        Assert.Equal(png, bytes);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void Css_url_token_parses_whole_token_only()
    {
        // PR #166 review P2 — url(...) parses as ONE complete token: multi-layer lists and
        // trailing tokens reject (→ the unsupported-form diagnostic path); quoted/unquoted
        // single tokens parse whole, commas INSIDE the URL included (data: URIs).
        Assert.True(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl(
            "url(data:image/png;base64,AAA)", out var u1));
        Assert.Equal("data:image/png;base64,AAA", u1);
        Assert.True(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl(
            "url(\"data:image/png;base64,AAA\")", out var u2));
        Assert.Equal("data:image/png;base64,AAA", u2);
        Assert.True(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl(
            "  url( 'x.png' )  ", out var u3));
        Assert.Equal("x.png", u3);

        Assert.False(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl("url(a),url(b)", out _));
        Assert.False(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl("url(a), url(b)", out _));
        Assert.False(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl("url(a) extra", out _));
        Assert.False(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl("url(\"a\" junk)", out _));
        Assert.False(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl("url(a", out _));
        Assert.False(NetPdf.Rendering.ImageResourceCache.TryParseCssUrl("url()", out _));
    }

    /// <summary>Records every URI the pipeline asks for and serves a tiny PNG.</summary>
    private sealed class RecordingLoader : IResourceLoader
    {
        public System.Collections.Generic.List<string> Requested { get; } = new();

        public ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct)
        {
            Requested.Add(uri.AbsoluteUri);
            return ValueTask.FromResult(new ResourceResponse
            {
                Content = SyntheticPng.BuildOpaqueRgb8(4, 4),
                MimeType = "image/png",
            });
        }
    }

    [Fact]
    public void Print_backgrounds_off_skips_background_image_fetch_but_not_img()
    {
        // PR #166 review P1 — PrintBackgrounds=false must not trigger loads / decode / budget
        // consumption for backgrounds the caller explicitly disabled; <img> is CONTENT and
        // still fetches.
        var loader = new RecordingLoader();
        var options = new HtmlPdfOptions
        {
            PrintBackgrounds = false,
            ResourceLoader = loader,
            SecurityPolicy = new SecurityPolicy { AllowHttpsScheme = true },
        };
        HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<img src=\"https://example.com/i.png\" style=\"display:block\">" +
            "<div style=\"width:32px;height:32px;background-image:url(https://example.com/b.png)\"></div>" +
            "</body></html>", options);
        Assert.Contains("https://example.com/i.png", loader.Requested);
        Assert.DoesNotContain("https://example.com/b.png", loader.Requested);

        // The control: backgrounds ON fetches both.
        var loaderOn = new RecordingLoader();
        HtmlPdf.Convert(
            "<!DOCTYPE html><html><body>" +
            "<img src=\"https://example.com/i.png\" style=\"display:block\">" +
            "<div style=\"width:32px;height:32px;background-image:url(https://example.com/b.png)\"></div>" +
            "</body></html>",
            new HtmlPdfOptions
            {
                PrintBackgrounds = true,
                ResourceLoader = loaderOn,
                SecurityPolicy = new SecurityPolicy { AllowHttpsScheme = true },
            });
        Assert.Contains("https://example.com/b.png", loaderOn.Requested);
    }
}
