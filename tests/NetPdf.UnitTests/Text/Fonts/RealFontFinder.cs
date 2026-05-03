// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.UnitTests.Text.Fonts;

/// <summary>
/// Test helper: locate a parseable single-face TTF/OTF on the host so tests that
/// genuinely need real font bytes can run without bundling a license-encumbered
/// fixture font. When no usable file is found the calling test should early-return
/// (xUnit 2.x has no native conditional-skip; the test remains green by no-op'ing).
/// </summary>
internal static class RealFontFinder
{
    /// <summary>
    /// Try to find any parseable single-face TTF/OTF in the host's standard font paths.
    /// Skips TTC / OTC collection files (Phase 1 does not parse collections).
    /// </summary>
    public static bool TryFindAnyTtf(out string path)
    {
        path = string.Empty;
        foreach (var dir in CandidateDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.ttf", SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var candidate in files)
            {
                if (TryParse(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        // macOS
        yield return "/System/Library/Fonts";
        yield return "/System/Library/Fonts/Supplemental";
        yield return "/Library/Fonts";
        // Linux
        yield return "/usr/share/fonts";
        yield return "/usr/local/share/fonts";
        // Windows
        var winDir = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrEmpty(winDir))
        {
            yield return Path.Combine(winDir, "Fonts");
        }
    }

    private static bool TryParse(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var _ = OpenTypeFont.Parse(bytes);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
