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
    };

    /// <summary>Upstream corpus invoices deliberately left out of the visual gate, with the reason (NO silent
    /// caps). Tailwind needs runtime JS; the Anvil invoice still carries remote images pending vendoring.</summary>
    public static IReadOnlyDictionary<string, string> ExcludedInvoices { get; } = new Dictionary<string, string>
    {
        ["02-tailwind-cdn.html"] = "Tailwind CDN needs runtime JS to emit utility CSS; renders unstyled without it.",
        ["03-tailwind-cdn-responsive.html"] = "Tailwind CDN (responsive) — same runtime-JS limitation.",
        ["04-anvil-running-elements.html"] = "Carries remote logo/heart images; vendor them as data: URIs into the harness corpus first.",
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
