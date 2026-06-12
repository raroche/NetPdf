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
}
