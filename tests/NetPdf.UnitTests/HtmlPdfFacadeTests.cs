// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO;
using System.Text;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests;

/// <summary>
/// Public-surface contract tests for the <see cref="HtmlPdf"/> facade. As of the
/// Phase 5 layout→PDF "Hello World" wiring the <c>Convert</c> family renders real
/// PDF bytes (single page, background fills); these tests pin the entry-point
/// contract — argument validation, the byte/stream/detailed shapes, and the
/// version surface. Rendering behavior (paint, determinism, diagnostics) is
/// covered by <c>HtmlPdfConvertTests</c>.
/// </summary>
public sealed class HtmlPdfFacadeTests
{
    private const string SampleHtml =
        "<!DOCTYPE html><html><body>" +
        "<div style=\"width:100px;height:50px;background-color:#102030\"></div>" +
        "</body></html>";

    [Fact]
    public void Version_reports_the_package_informational_version_not_the_assembly_version()
    {
        // The fix from the Phase 1 global review: HtmlPdf.Version must read
        // AssemblyInformationalVersionAttribute (which carries the prerelease tag —
        // "0.7.0-beta+<sha>") rather than AssemblyName.Version (which gives the
        // 4-part assembly version "0.1.0.0" and silently drops the prerelease).
        var version = HtmlPdf.Version;

        Assert.False(string.IsNullOrEmpty(version));
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+\.\d+$"),
            $"HtmlPdf.Version returned the 4-part assembly version '{version}' — should be the " +
            "informational/package version (with the prerelease tag preserved).");
        // When this assertion fails on a legitimate version bump, update the expectation.
        // ReleaseVersionParityTests guards that this matches Directory.Build.props +
        // build/version.json + the CHANGELOG's latest version heading.
        // Pin the EXACT prerelease (start + a `+build` metadata boundary or end) — a loose Contains would also
        // pass `0.9.0-rc10` or an unrelated string carrying both fragments (PR-258 Copilot).
        Assert.Matches(@"^0\.9\.0-rc1(\+.*)?$", version);
    }

    [Fact]
    public void Convert_string_returns_pdf_bytes()
    {
        var bytes = HtmlPdf.Convert(SampleHtml);

        Assert.NotNull(bytes);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Convert_span_overload_returns_pdf_bytes()
    {
        var bytes = HtmlPdf.Convert(SampleHtml.AsSpan());

        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    [Fact]
    public async Task ConvertAsync_returns_pdf_bytes()
    {
        var bytes = await HtmlPdf.ConvertAsync(SampleHtml);

        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    [Fact]
    public async Task ConvertAsync_stream_overload_writes_the_pdf_to_the_stream()
    {
        using var stream = new MemoryStream();

        await HtmlPdf.ConvertAsync(SampleHtml, stream);

        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 0);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    [Fact]
    public void ConvertDetailed_returns_a_result_carrying_the_pdf()
    {
        var result = HtmlPdf.ConvertDetailed(SampleHtml);

        Assert.NotNull(result.Pdf);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(result.Pdf, 0, 5));
        Assert.Equal(1, result.PageCount);
        Assert.NotNull(result.Warnings);
    }

    [Fact]
    public void Convert_throws_ArgumentNullException_for_null_html()
    {
        Assert.Throws<ArgumentNullException>(() => HtmlPdf.Convert(html: null!));
    }

    [Fact]
    public async Task ConvertAsync_string_overload_throws_ArgumentNullException_for_null_html()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await HtmlPdf.ConvertAsync(html: null!));
    }

    [Fact]
    public async Task ConvertAsync_stream_overload_throws_ArgumentNullException_for_null_output()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await HtmlPdf.ConvertAsync(SampleHtml, output: null!));
    }

    [Fact]
    public void ConvertDetailed_throws_ArgumentNullException_for_null_html()
    {
        Assert.Throws<ArgumentNullException>(() => HtmlPdf.ConvertDetailed(html: null!));
    }

    // ── HtmlPdfOptions.Timeout (PR #118 review P1) ───────────────────────────

    [Fact]
    public void Convert_with_zero_timeout_throws_TimeoutException()
    {
        var options = new HtmlPdfOptions { Timeout = TimeSpan.Zero };
        Assert.Throws<TimeoutException>(() => HtmlPdf.Convert(SampleHtml, options));
    }

    [Fact]
    public async Task ConvertAsync_with_zero_timeout_throws_TimeoutException()
    {
        var options = new HtmlPdfOptions { Timeout = TimeSpan.Zero };
        await Assert.ThrowsAsync<TimeoutException>(async () => await HtmlPdf.ConvertAsync(SampleHtml, options));
    }

    [Fact]
    public void ConvertDetailed_with_zero_timeout_throws_TimeoutException()
    {
        var options = new HtmlPdfOptions { Timeout = TimeSpan.Zero };
        Assert.Throws<TimeoutException>(() => HtmlPdf.ConvertDetailed(SampleHtml, options));
    }

    [Fact]
    public async Task ConvertAsync_honors_caller_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await HtmlPdf.ConvertAsync(SampleHtml, options: null, cts.Token));
    }

    [Fact]
    public void Convert_with_a_generous_timeout_still_succeeds()
    {
        var options = new HtmlPdfOptions { Timeout = TimeSpan.FromSeconds(30) };
        var bytes = HtmlPdf.Convert(SampleHtml, options);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }
}
