// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.SystemFonts;

/// <summary>
/// macOS system-font enumerator. Walks the three standard font directories Apple
/// documents in <i>System Programming Guide — Fonts</i>: <c>/System/Library/Fonts</c>
/// (system-supplied), <c>/Library/Fonts</c> (administrator-installed), and
/// <c>~/Library/Fonts</c> (per-user). Subdirectories are recursed (recent macOS versions
/// place faces under <c>Supplemental</c> and <c>Disabled</c> subfolders).
/// </summary>
internal sealed class MacOsSystemFontEnumerator : SystemFontEnumerator
{
    protected override IEnumerable<string> FontDirectories
    {
        get
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "Library", "Fonts");
            }
        }
    }
}
