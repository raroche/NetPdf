// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using SkiaSharp;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>Phase 4 PR 8 — the Chrome ORACLE side of the visual-regression harness, in C# (a pure-.NET
/// alternative to the Python <c>docker/generate-references.py</c>). Drives an installed Playwright Chromium to
/// print a self-contained corpus invoice to PDF, then rasterizes EVERY page at the harness DPI through the SAME
/// <see cref="IPdfRasterizer"/> (PDFium) the diff runner uses — so the reference and the candidate share one
/// rasterizer, eliminating a whole class of cross-rasterizer drift. Writes one PNG per page
/// (<c>&lt;stem&gt;-page-NNN.png</c>, 1-based) matching the runner's per-page contract.
///
/// NetPdf never bundles a browser: this runs deliberately, off the render path, and the produced PNGs are the
/// Chrome oracle. The CANONICAL committed references must still be generated on Linux/CI (macOS Chrome drifts on
/// font hinting / AA), so this generator's job in the sandbox is to VALIDATE the pipeline end-to-end, not to
/// commit references from a developer machine.</summary>
internal static class ChromeReferenceGenerator
{
    /// <summary>Thrown when Playwright / the pinned Chromium can't launch here (no browser installed, a sandbox
    /// restriction, a driver mismatch). The caller treats it as "oracle unavailable" and skips, exactly like a
    /// missing PDF rasterizer — it never masks a real diff.</summary>
    internal sealed class OracleUnavailableException(string message, Exception? inner = null)
        : Exception(message, inner);

    /// <summary>Print <paramref name="html"/> to PDF via Chromium and rasterize each page to
    /// <paramref name="outputDir"/>/<paramref name="stem"/>-page-NNN.png at <paramref name="dpi"/> DPI. Returns
    /// the number of pages written. Throws <see cref="OracleUnavailableException"/> if Chromium can't launch.</summary>
    public static async Task<int> GenerateAsync(
        string html, IPdfRasterizer rasterizer, string outputDir, string stem, int dpi)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(rasterizer);
        Directory.CreateDirectory(outputDir);

        byte[] pdf;
        try
        {
            using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            // The Microsoft.Playwright NuGet pins a specific Chromium revision; when only a DIFFERENT revision
            // is installed (a common sandbox/CI mismatch), the default launch can't find its expected binary.
            // Discover any installed chrome-headless-shell / Chrome-for-Testing and drive it via ExecutablePath
            // so a revision skew doesn't block reference generation. Falls back to the default binary if none
            // is discovered (the driver then errors → OracleUnavailable, handled below).
            var launchOptions = new BrowserTypeLaunchOptions();
            if (DiscoverChromiumExecutable() is { } exe) launchOptions.ExecutablePath = exe;
            await using var browser = await playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
            var page = await browser.NewPageAsync().ConfigureAwait(false);
            // Self-contained corpus invoices (data: URIs) → SetContent is enough; NetworkIdle guards any async
            // sub-resource. print_background + prefer_css_page_size mirror the Python oracle + NetPdf's own
            // @page sizing so the two PDFs share a page geometry.
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
            }).ConfigureAwait(false);
            pdf = await page.PdfAsync(new PagePdfOptions
            {
                PrintBackground = true,
                PreferCSSPageSize = true,
            }).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw new OracleUnavailableException(
                "Chromium/Playwright could not launch to generate a Chrome reference: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OracleUnavailableException)
        {
            // The Playwright driver spawns a node process; a sandbox that blocks it surfaces various exception
            // types. Treat any launch-time failure as oracle-unavailable rather than a test failure.
            throw new OracleUnavailableException(
                "Chromium/Playwright is unavailable in this environment: " + ex.Message, ex);
        }

        var pages = rasterizer.RasterizeAllPages(pdf, dpi);
        for (var i = 0; i < pages.Count; i++)
            WritePng(pages[i], Path.Combine(outputDir, $"{stem}-page-{i + 1:D3}.png"));
        return pages.Count;
    }

    /// <summary>Find an installed Chromium executable to drive when the NuGet-pinned revision isn't the one
    /// present. Honors <c>PLAYWRIGHT_CHROMIUM_EXECUTABLE</c> first, then scans the Playwright browser cache
    /// (<c>PLAYWRIGHT_BROWSERS_PATH</c> or the per-OS default) for the newest <c>chrome-headless-shell</c> (or
    /// full Chrome-for-Testing) binary. Returns <see langword="null"/> when none is found (use the default).</summary>
    internal static string? DiscoverChromiumExecutable()
    {
        var explicitExe = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE");
        if (!string.IsNullOrEmpty(explicitExe) && File.Exists(explicitExe)) return explicitExe;

        var cacheRoot = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrEmpty(cacheRoot))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            cacheRoot = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "ms-playwright")
                : OperatingSystem.IsWindows() ? Path.Combine(home, "AppData", "Local", "ms-playwright")
                : Path.Combine(home, ".cache", "ms-playwright");
        }
        if (!Directory.Exists(cacheRoot)) return null;

        // Prefer the lighter headless shell; fall back to the full browser. Newest revision (highest suffix)
        // first, so a matching-ish build is chosen when several are present.
        foreach (var (prefix, exeNames) in new[]
                 {
                     ("chromium_headless_shell", new[] { "chrome-headless-shell", "headless_shell" }),
                     ("chromium", new[] { "Google Chrome for Testing", "chrome", "Chromium" }),
                 })
        {
            foreach (var dir in SortByRevisionDescending(Directory.GetDirectories(cacheRoot, prefix + "-*")))
                foreach (var exe in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    // Match case-insensitively against the file name WITH and WITHOUT its extension, so a
                    // Windows binary (chrome.exe / chrome-headless-shell.exe) matches the same target names as
                    // the extension-less Unix/macOS binary. Ordinal comparison of "chrome" vs "chrome.exe"
                    // would otherwise miss every Windows install and wrongly report the oracle unavailable.
                    var name = Path.GetFileName(exe);
                    var stem = Path.GetFileNameWithoutExtension(exe);
                    if (System.Array.Exists(exeNames, n =>
                            string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)
                            || string.Equals(n, stem, System.StringComparison.OrdinalIgnoreCase)))
                        return exe; // first match at the highest revision wins
                }
        }
        return null;
    }

    private static string[] SortByRevisionDescending(string[] dirs)
    {
        System.Array.Sort(dirs, (a, b) => RevisionOf(b).CompareTo(RevisionOf(a)));
        return dirs;

        static int RevisionOf(string dir)
        {
            var name = Path.GetFileName(dir);
            var dash = name.LastIndexOf('-');
            return dash >= 0 && int.TryParse(name[(dash + 1)..], out var r) ? r : 0;
        }
    }

    /// <summary>Encode an 8-bit RGBA <see cref="RasterImage"/> to a PNG file (SkiaSharp — read-only PNG write,
    /// the approved raster role). Unpremultiplied, matching <c>VisualHarness.LoadPng</c>'s read.</summary>
    internal static void WritePng(RasterImage image, string path)
    {
        image.EnsureValid();
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(image.Rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            // InstallPixels returns false on an incompatible info/stride; ignoring it would encode an
            // empty/invalid PNG (or throw later with a less actionable error) — fail fast with the dimensions.
            if (!bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes))
                throw new InvalidOperationException(
                    $"SKBitmap.InstallPixels failed for a {image.Width}x{image.Height} RGBA image writing '{path}'.");
            using var img = SKImage.FromBitmap(bitmap);
            // Encode returns null if the PNG encoder fails; guard it like InstallPixels rather than NRE on SaveTo.
            using var data = img.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException(
                    $"PNG encoding failed for a {image.Width}x{image.Height} image writing '{path}'.");
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }
        finally
        {
            handle.Free();
        }
    }
}
