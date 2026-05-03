// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.SystemFonts;

/// <summary>
/// Linux / Alpine / generic-Unix system-font enumerator. Walks the standard freedesktop
/// font directories (<c>/usr/share/fonts</c>, <c>/usr/local/share/fonts</c>) and the
/// per-user locations (<c>~/.fonts</c>, <c>~/.local/share/fonts</c>). Alpine /
/// musl-based distributions use the same conventions and are covered without an
/// extra subclass.
/// </summary>
internal sealed class LinuxSystemFontEnumerator : SystemFontEnumerator
{
    protected override IEnumerable<string> FontDirectories
    {
        get
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                // Legacy ~/.fonts and the freedesktop-spec ~/.local/share/fonts.
                yield return Path.Combine(home, ".fonts");
                yield return Path.Combine(home, ".local", "share", "fonts");
            }
            // XDG_DATA_HOME override — if set, prefer it over the implicit ~/.local/share.
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdg))
            {
                yield return Path.Combine(xdg, "fonts");
            }
        }
    }
}
