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
        // Pin the EXACT version (start + an optional `+build` metadata boundary or end) — a loose Contains
        // would also pass `1.0.10` or an unrelated string carrying the fragment (PR-258 Copilot). Stable
        // releases from 1.0.0 on carry no prerelease suffix. Bump this on each release (guarded by
        // ReleaseVersionParityTests, which keeps the three version surfaces in agreement).
        Assert.Matches(@"^1\.2\.0(\+.*)?$", version);
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

    // ── SecurityPolicy.RenderTimeout — the policy's default render cap ───────

    [Fact]
    public void UntrustedHtml_has_a_30_second_default_render_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SecurityPolicy.UntrustedHtml.RenderTimeout);
    }

    [Fact]
    public void Trusted_policies_have_no_default_render_timeout()
    {
        // Trusted rendering must stay uncapped by default (a long report is legitimate).
        Assert.Null(SecurityPolicy.SafeDefault.RenderTimeout);
        Assert.Null(SecurityPolicy.TrustedTemplate.RenderTimeout);
        Assert.Null(new SecurityPolicy().RenderTimeout);
    }

    [Fact]
    public void Policy_render_timeout_applies_when_options_timeout_is_null()
    {
        var options = new HtmlPdfOptions { SecurityPolicy = new SecurityPolicy { RenderTimeout = TimeSpan.Zero } };
        var ex = Assert.Throws<TimeoutException>(() => HtmlPdf.Convert(SampleHtml, options));
        Assert.Contains("SecurityPolicy.RenderTimeout", ex.Message);
    }

    [Fact]
    public async Task Policy_render_timeout_applies_to_ConvertAsync()
    {
        var options = new HtmlPdfOptions { SecurityPolicy = new SecurityPolicy { RenderTimeout = TimeSpan.Zero } };
        await Assert.ThrowsAsync<TimeoutException>(async () => await HtmlPdf.ConvertAsync(SampleHtml, options));
    }

    [Fact]
    public void Explicit_options_timeout_overrides_the_policy_default()
    {
        // The policy alone would fail immediately; the caller's explicit cap wins.
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = new SecurityPolicy { RenderTimeout = TimeSpan.Zero },
            Timeout = TimeSpan.FromSeconds(30),
        };
        var bytes = HtmlPdf.Convert(SampleHtml, options);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Explicit_options_timeout_error_names_its_source()
    {
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = new SecurityPolicy { RenderTimeout = TimeSpan.FromSeconds(30) },
            Timeout = TimeSpan.Zero,
        };
        var ex = Assert.Throws<TimeoutException>(() => HtmlPdf.Convert(SampleHtml, options));
        Assert.Contains("HtmlPdfOptions.Timeout", ex.Message);
    }

    [Fact]
    public void Infinite_options_timeout_removes_the_policy_default()
    {
        // Timeout.InfiniteTimeSpan is the opt-out: no cap at all, even under a capped policy.
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = new SecurityPolicy { RenderTimeout = TimeSpan.Zero },
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        var bytes = HtmlPdf.Convert(SampleHtml, options);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    [Fact]
    public void Infinite_policy_render_timeout_means_no_cap()
    {
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = new SecurityPolicy { RenderTimeout = System.Threading.Timeout.InfiniteTimeSpan },
        };
        var bytes = HtmlPdf.Convert(SampleHtml, options);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(bytes, 0, 5));
    }

    private const string RemoteImageHtml =
        "<!DOCTYPE html><html><body><img src=\"https://example.com/i.png\" style=\"display:block\"></body></html>";

    /// <summary>A loader that cancels for its OWN reason, unrelated to any render timer.</summary>
    private sealed class SelfCancellingLoader : IResourceLoader
    {
        public ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct) =>
            throw new OperationCanceledException("the loader gave up on its own");
    }

    /// <summary>A loader that ignores its token for cancellation purposes: it never throws, and returns
    /// normally only once the deadline has PASSED (it waits until the token is cancelled, up to
    /// <paramref name="maxWait"/>). Waiting on the token rather than a fixed delay keeps the test
    /// deterministic when a busy test host fires the render timer late.</summary>
    private sealed class SlowLoader(TimeSpan maxWait) : IResourceLoader
    {
        public async ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested && waited.Elapsed < maxWait)
                await Task.Delay(10, CancellationToken.None);
            return new ResourceResponse { Content = ReadOnlyMemory<byte>.Empty };
        }
    }

    [Fact]
    public async Task Cancellation_the_timer_did_not_cause_is_not_reported_as_a_timeout()
    {
        // PR #380 review: with a policy cap in force, an unrelated cancellation used to be relabeled as
        // "SecurityPolicy.RenderTimeout" even though the timer never fired. It must propagate unchanged.
        var options = new HtmlPdfOptions
        {
            ResourceLoader = new SelfCancellingLoader(),
            SecurityPolicy = new SecurityPolicy { AllowHttpsScheme = true, RenderTimeout = TimeSpan.FromMinutes(5) },
        };
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await HtmlPdf.ConvertAsync(RemoteImageHtml, options));
        Assert.Equal("the loader gave up on its own", ex.Message);
    }

    [Fact]
    public async Task A_stage_that_ignores_the_token_past_the_deadline_still_times_out()
    {
        // The loader ignores cancellation and returns after the policy cap has passed. The render must
        // not produce a PDF past the cap: the next cancellation check reports the timeout.
        var options = new HtmlPdfOptions
        {
            ResourceLoader = new SlowLoader(TimeSpan.FromSeconds(10)),
            SecurityPolicy = new SecurityPolicy
            {
                AllowHttpsScheme = true,
                RenderTimeout = TimeSpan.FromMilliseconds(100),
            },
        };
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            async () => await HtmlPdf.ConvertAsync(RemoteImageHtml, options));
        Assert.Contains("SecurityPolicy.RenderTimeout", ex.Message);
    }

    [Fact]
    public void UntrustedHtml_with_no_options_timeout_renders_a_normal_document()
    {
        // Integration: the 30 s default must not affect an ordinary render.
        var options = new HtmlPdfOptions { SecurityPolicy = SecurityPolicy.UntrustedHtml };
        var result = HtmlPdf.ConvertDetailed(SampleHtml, options);
        Assert.StartsWith("%PDF-", Encoding.Latin1.GetString(result.Pdf, 0, 5));
        Assert.Equal(1, result.PageCount);
    }
}
