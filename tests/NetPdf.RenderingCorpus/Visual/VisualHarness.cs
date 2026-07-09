// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Shared paths + constants + the reference-PNG loader for the visual-regression harness (PR 8).
/// Resolves the repo layout from the test assembly so the runner can find the corpus HTML and the committed
/// reference PNGs regardless of the working directory.</summary>
public static class VisualHarness
{
    /// <summary>The DPI BOTH renderers rasterize at. The reference PNGs MUST be generated at this DPI (the
    /// generator runbook reads this same value) so the diff compares equal-size rasters.</summary>
    public const int Dpi = 300;

    /// <summary>Invoices the visual gate diffs against Chrome. These live in the harness's OWN
    /// <see cref="CorpusDir"/> and MUST be self-contained (no remote http(s) resources — a remote asset is
    /// blocked by NetPdf's SafeDefault yet fetched by Chrome, a nondeterminism the gate can't tolerate;
    /// PR-242 review [P1]). The Tailwind-CDN invoices are excluded entirely (runtime-JS); the upstream
    /// RealDocuments invoices with remote logos are not added here until vendored (the maintainer copies +
    /// inlines their assets, like <c>01-classic-pure-css.html</c> here). A guard test enforces no-remote.</summary>
    public static IReadOnlyList<string> DiffableInvoices { get; } = new[]
    {
        "01-classic-pure-css.html",
        // Vendored self-contained copy in CorpusDir — the two remote raster logos/heart of the upstream
        // Anvil invoice are replaced by inline SVG data: URIs, so it renders fetch-free and deterministically
        // (running elements + @page margin boxes + page counters exercise paged-media). RH-4.
        "04-anvil-running-elements.html",
    };

    /// <summary>Upstream corpus invoices deliberately left out of the visual gate, with the reason (NO silent
    /// caps). Only the Tailwind-CDN invoices remain excluded — they need runtime JS to emit their utility CSS,
    /// so they render unstyled and can't be diffed against a browser reference.</summary>
    public static IReadOnlyDictionary<string, string> ExcludedInvoices { get; } = new Dictionary<string, string>
    {
        ["02-tailwind-cdn.html"] = "Tailwind CDN needs runtime JS to emit utility CSS; renders unstyled without it.",
        ["03-tailwind-cdn-responsive.html"] = "Tailwind CDN (responsive) — same runtime-JS limitation.",
    };

    /// <summary>The repo root (the directory containing <c>NetPdf.slnx</c>), located by walking up from the
    /// test assembly's base directory.</summary>
    public static string RepoRoot { get; } = LocateRepoRoot();

    /// <summary>The harness's OWN self-contained corpus directory — vendored copies (remote assets inlined)
    /// so the visual gate is deterministic and fetch-free, decoupled from the upstream RealDocuments corpus
    /// (whose byte-identity baselines we must not perturb).</summary>
    public static string CorpusDir => Path.Combine(RepoRoot, "tests", "NetPdf.RenderingCorpus", "corpus");

    /// <summary>Where committed reference PNGs live — one per PAGE, named <c>&lt;stem&gt;-page-NNN.png</c>
    /// (1-based, zero-padded). Absent until the maintainer generates them — the gate is inert while empty.</summary>
    public static string ReferenceDir => Path.Combine(RepoRoot, "tests", "NetPdf.RenderingCorpus", "references");

    /// <summary>The committed, PINNED font pack (DejaVu Sans, RIBBI) both renderers share so the diff
    /// measures LAYOUT-ENGINE differences, not font drift between the .NET host and the pinned-Chrome
    /// reference generator (which is aliased to the SAME DejaVu via <c>docker/Dockerfile</c>'s fontconfig).
    /// NetPdf renders with <see cref="PinnedFonts"/>; Chrome resolves the corpus families to DejaVu Sans.</summary>
    public static string FontsDir => Path.Combine(RepoRoot, "tests", "NetPdf.RenderingCorpus", "fonts");

    /// <summary>A fresh <see cref="IFontResolver"/> that serves the pinned DejaVu Sans pack for EVERY family,
    /// so the NetPdf side of the diff is font-deterministic and matches the Chrome reference oracle.</summary>
    public static IFontResolver PinnedFonts() => new PinnedFontResolver();

    /// <summary>The page size NetPdf must render the corpus at to match the reference oracle. The generator
    /// drives Chrome's <c>page.pdf(prefer_css_page_size=True)</c>, and the corpus invoices declare no explicit
    /// <c>@page size</c>, so Chrome falls back to its default US <b>Letter</b> — 2550×3301 px at 300 dpi.
    /// NetPdf's own default is A4 (2481×3509), so without this the rasters differ in size and the diff can't
    /// even run. Pinned here so a future page-size change is a single edit shared by both diff render sites.</summary>
    public static PageSize ReferencePageSize => PageSize.Letter;

    /// <summary>Sub-pixel page-size rounding slack (px). Chrome and NetPdf can rasterize the SAME logical
    /// page size (US Letter, 792 pt tall) to pixel heights differing by a couple of px at 300 dpi — Chrome
    /// rounds 792 pt → 3301, NetPdf → 3299. A delta THIS small is a rounding artifact, not a layout bug.</summary>
    public const int PageRoundingSlackPx = 2;

    /// <summary>Crop both rasters to their common minimum dimensions when they differ by at most
    /// <see cref="PageRoundingSlackPx"/> on each axis (dropping only page-edge margin whitespace), so the
    /// page-for-page diff isn't defeated by rasterization rounding. A LARGER delta is a real page-size bug
    /// and is returned unchanged, so <see cref="PixelDiff.Compare"/> still surfaces it as a size mismatch.</summary>
    public static (RasterImage Expected, RasterImage Actual) ReconcilePageRounding(RasterImage expected, RasterImage actual)
    {
        var dw = Math.Abs(expected.Width - actual.Width);
        var dh = Math.Abs(expected.Height - actual.Height);
        if ((dw == 0 && dh == 0) || dw > PageRoundingSlackPx || dh > PageRoundingSlackPx)
            return (expected, actual);
        var w = Math.Min(expected.Width, actual.Width);
        var h = Math.Min(expected.Height, actual.Height);
        return (expected.CropTo(w, h), actual.CropTo(w, h));
    }

    /// <summary>The reference PNG path for a 1-based page of an invoice (e.g.
    /// <c>01-classic-pure-css.html</c> page 1 → <c>references/01-classic-pure-css-page-001.png</c>).</summary>
    public static string ReferencePagePath(string invoiceFileName, int pageNumber) =>
        Path.Combine(ReferenceDir, $"{Path.GetFileNameWithoutExtension(invoiceFileName)}-page-{pageNumber:D3}.png");

    /// <summary>The committed reference page PNGs for an invoice, in page order (empty when none committed).</summary>
    public static IReadOnlyList<string> ReferencePagePaths(string invoiceFileName)
    {
        if (!Directory.Exists(ReferenceDir)) return Array.Empty<string>();
        var stem = Path.GetFileNameWithoutExtension(invoiceFileName);
        var files = Directory.GetFiles(ReferenceDir, $"{stem}-page-*.png");
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>Whether at least one reference page is committed for the invoice.</summary>
    public static bool ReferenceExists(string invoiceFileName) => ReferencePagePaths(invoiceFileName).Count > 0;

    /// <summary>Read a harness-corpus invoice's HTML.</summary>
    public static string ReadInvoiceHtml(string invoiceFileName) =>
        File.ReadAllText(Path.Combine(CorpusDir, invoiceFileName));

    // FETCHED remote resources only — image / script <c>src</c>, CSS <c>url(...)</c>, and <c>&lt;link&gt;</c>
    // hrefs (stylesheets / fonts). A navigation <c>&lt;a href&gt;</c> is NOT a fetched/painted resource and
    // is deliberately not matched.
    private static readonly Regex[] RemoteResourcePatterns =
    [
        new(@"\bsrc\s*=\s*[""']?(https?://[^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"url\(\s*[""']?(https?://[^""')\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"<link\b[^>]*\bhref\s*=\s*[""']?(https?://[^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>The remote http(s) resource URLs an HTML document would FETCH (img/script <c>src</c>, CSS
    /// <c>url()</c>, <c>&lt;link&gt;</c> href) — used to keep diffable invoices self-contained.</summary>
    public static IReadOnlyList<string> RemoteResourceUrls(string html)
    {
        var found = new List<string>();
        foreach (var pattern in RemoteResourcePatterns)
            foreach (Match m in pattern.Matches(html))
                found.Add(m.Groups[1].Value);
        return found;
    }

    /// <summary>Decode a PNG file to an RGBA <see cref="RasterImage"/> (unpremultiplied, 8-bit/channel).</summary>
    public static RasterImage LoadPng(string path)
    {
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidOperationException($"could not open PNG: {path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidOperationException($"could not decode PNG ({result}): {path}");
        return new RasterImage(info.Width, info.Height, bmp.Bytes);
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NetPdf.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        // Fall back to the working directory; callers that need the corpus will surface a clear file error.
        return Directory.GetCurrentDirectory();
    }
}
