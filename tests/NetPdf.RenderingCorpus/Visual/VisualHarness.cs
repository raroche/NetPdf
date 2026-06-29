// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>Corpus invoices that are meaningfully diffable against Chrome. The two Tailwind-CDN files are
    /// EXCLUDED (see <see cref="ExcludedInvoices"/>) because they need runtime JS to generate their utility
    /// CSS, so neither Chrome-without-JS nor NetPdf renders them as authored (CLAUDE.md).</summary>
    public static IReadOnlyList<string> DiffableInvoices { get; } = new[]
    {
        "01-classic-pure-css.html",
        "04-anvil-running-elements.html",
    };

    /// <summary>Invoices deliberately left out of the visual gate, with the reason (NO silent caps).</summary>
    public static IReadOnlyDictionary<string, string> ExcludedInvoices { get; } = new Dictionary<string, string>
    {
        ["02-tailwind-cdn.html"] = "Tailwind CDN needs runtime JS to emit utility CSS; renders unstyled without it.",
        ["03-tailwind-cdn-responsive.html"] = "Tailwind CDN (responsive) — same runtime-JS limitation.",
    };

    /// <summary>The repo root (the directory containing <c>NetPdf.slnx</c>), located by walking up from the
    /// test assembly's base directory.</summary>
    public static string RepoRoot { get; } = LocateRepoRoot();

    /// <summary>The corpus invoice HTML directory.</summary>
    public static string CorpusDir => Path.Combine(RepoRoot, "tests", "NetPdf.RealDocuments", "Corpus", "Invoices");

    /// <summary>Where committed reference PNGs live (one per diffable invoice, <c>&lt;stem&gt;.png</c>).
    /// Absent until the maintainer generates them — the runner skips while empty.</summary>
    public static string ReferenceDir => Path.Combine(RepoRoot, "tests", "NetPdf.RenderingCorpus", "references");

    /// <summary>The reference PNG path for an invoice file name (e.g. <c>01-classic-pure-css.html</c> →
    /// <c>references/01-classic-pure-css.png</c>).</summary>
    public static string ReferencePath(string invoiceFileName) =>
        Path.Combine(ReferenceDir, Path.GetFileNameWithoutExtension(invoiceFileName) + ".png");

    /// <summary>Read a corpus invoice's HTML.</summary>
    public static string ReadInvoiceHtml(string invoiceFileName) =>
        File.ReadAllText(Path.Combine(CorpusDir, invoiceFileName));

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
