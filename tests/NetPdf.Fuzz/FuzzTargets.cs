// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf;                // HtmlPdf, HtmlPdfOptions, HtmlPdfException, UriSafetyValidator, SecurityPolicy
using NetPdf.Pdf.Images;     // ImageSafetyValidator
using NetPdf.Shaping;        // FontResolutionException
using NetPdf.Svg;            // SvgRasterizer (internal — reached via InternalsVisibleTo)
using NetPdf.Text.Fonts;     // FontSafetyValidator

namespace NetPdf.Fuzz;

/// <summary>
/// The security-critical entry points exercised by the fuzz harness. Each target is a
/// <b>total function</b> over arbitrary bytes: it must never throw an <i>unexpected</i>
/// exception and must never hang. The four validators
/// (<see cref="UriSafetyValidator"/>, <see cref="FontSafetyValidator"/>,
/// <see cref="ImageSafetyValidator"/>, <see cref="SvgRasterizer"/>) are designed to be
/// exception-free over any input — so ANY throw is a finding. The full-pipeline target
/// (<see cref="HtmlPdf.Convert(string, HtmlPdfOptions?)"/>) is allowed a closed set of
/// <see cref="IsSanctioned"/> exceptions (typed conversion failure, font resolution,
/// timeout/cancellation); anything else is a finding.
/// </summary>
/// <remarks>
/// Shared by both harness modes: the libFuzzer callback (an unhandled exception = a
/// libFuzzer crash) and the deterministic smoke runner (<c>--smoke</c>, which classifies
/// the exception via <see cref="IsSanctioned"/>). Keeping the target bodies here means the
/// two modes fuzz the <i>same</i> code — see <c>docs/security/fuzzing.md</c>.
/// </remarks>
internal static class FuzzTargets
{
    internal enum Target
    {
        HtmlConvert,
        Uri,
        Font,
        Image,
        Svg,
    }

    internal static readonly Target[] All = Enum.GetValues<Target>();

    /// <summary>
    /// libFuzzer dispatch: the first byte selects a target, the remainder is the payload.
    /// Lets a single libFuzzer campaign reach every target from one flat input stream.
    /// </summary>
    internal static void RunDispatch(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var target = All[data[0] % All.Length];
        Run(target, data[1..]);
    }

    /// <summary>Drive one target with a payload. Sanctioned exceptions are swallowed here;
    /// everything else propagates (a crash under libFuzzer, a finding under the smoke runner).</summary>
    internal static void Run(Target target, ReadOnlySpan<byte> payload)
    {
        switch (target)
        {
            case Target.HtmlConvert:
                FuzzHtmlConvert(payload);
                break;
            case Target.Uri:
                FuzzUri(payload);
                break;
            case Target.Font:
                FuzzFont(payload);
                break;
            case Target.Image:
                FuzzImage(payload);
                break;
            case Target.Svg:
                FuzzSvg(payload);
                break;
        }
    }

    /// <summary>
    /// The closed set of exceptions the full-pipeline target may legitimately raise on
    /// hostile input. A throw of any OTHER type from <see cref="HtmlPdf.Convert(string, HtmlPdfOptions?)"/>
    /// is a defect (a fuzz finding). The validators are held to a stricter contract — they
    /// must not throw at all — so callers of those targets do NOT consult this method.
    /// </summary>
    internal static bool IsSanctioned(Exception ex) => ex switch
    {
        HtmlPdfException => true,          // typed conversion failure / strict-mode unsupported feature
        FontResolutionException => true,   // font resolution failed on a substituted/embedded face
        OperationCanceledException => true, // render Timeout / CancellationToken (incl. TaskCanceledException)
        TimeoutException => true,          // render Timeout surfaced as a plain timeout
        _ => false,
    };

    // --- Target: the full HTML → PDF pipeline (untrusted-HTML profile) ------------------
    //
    // The hostile deployment (§2 use case #2): fully attacker-controlled HTML, no ambient
    // resource loader, a bounded render time. This is the broadest target — it reaches HTML
    // parsing, the URL-strip pass, the cascade, layout, paint, and the PDF writer + preflight.
    private static void FuzzHtmlConvert(ReadOnlySpan<byte> payload)
    {
        var html = DecodeUtf8(payload);
        var options = new HtmlPdfOptions
        {
            SecurityPolicy = SecurityPolicy.UntrustedHtml, // no http/file/data fetch, tight budgets
            ResourceLoader = null,                          // no ambient network/filesystem reach
            Timeout = TimeSpan.FromSeconds(5),              // bound render time (also honors the token)
        };

        try
        {
            _ = HtmlPdf.Convert(html, options);
        }
        catch (Exception ex) when (IsSanctioned(ex))
        {
            // Expected, well-typed rejection of hostile/unsupported input — not a finding.
        }
    }

    // --- Target: URI safety (SSRF / LFI choke point) ------------------------------------
    //
    // UriSafetyValidator.Validate must be total over any parseable URI. We build the Uri
    // ourselves (a malformed string is not the validator's concern) and assert only that the
    // validate call itself never throws — so no exception is caught here.
    private static void FuzzUri(ReadOnlySpan<byte> payload)
    {
        var text = DecodeUtf8(payload);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return;
        }

        _ = UriSafetyValidator.Validate(uri, SecurityPolicy.SafeDefault);
        _ = UriSafetyValidator.Validate(uri, SecurityPolicy.UntrustedHtml);
    }

    // --- Target: font pre-decode validator (V4/V5 — runs before HarfBuzz) ----------------
    private static void FuzzFont(ReadOnlySpan<byte> payload)
    {
        _ = FontSafetyValidator.Validate(payload);
    }

    // --- Target: image pre-decode validator (V5 — runs before the raster decoder) --------
    private static void FuzzImage(ReadOnlySpan<byte> payload)
    {
        _ = ImageSafetyValidator.Validate(payload);
    }

    // --- Target: SVG parse + rasterize (V3 XXE + DoS) ------------------------------------
    //
    // SvgRasterizer.TryRender is XXE-hardened (DTD prohibited, no resolver) and DoS-bounded
    // (depth / element / char caps). It returns null on any parse failure, so a throw is a
    // finding. The return type is internal to NetPdf.Pdf, so the result is discarded via the
    // expression statement (no need to name the type here).
    private static void FuzzSvg(ReadOnlySpan<byte> payload)
    {
        SvgRasterizer.TryRender(payload.ToArray(), out _);
    }

    /// <summary>UTF-8 decode that never throws on invalid bytes (replacement-char fallback),
    /// so decoding can't itself masquerade as a target crash.</summary>
    private static string DecodeUtf8(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? string.Empty : Encoding.UTF8.GetString(bytes);
}
