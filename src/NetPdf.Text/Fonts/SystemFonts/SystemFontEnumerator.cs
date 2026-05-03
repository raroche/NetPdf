// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.InteropServices;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Text.Fonts.SystemFonts;

/// <summary>
/// Base class for platform-specific system-font enumeration. Subclasses provide the list
/// of well-known font directories for their platform; the base class walks each directory
/// (recursively) and parses the <c>name</c> + <c>OS/2</c> tables of every reachable
/// <c>.ttf</c> / <c>.otf</c> file to materialize <see cref="SystemFontEntry"/> records.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design note.</b> Enumeration intentionally does <i>not</i> hold the full
/// <see cref="FontFace"/> in memory — the index keeps only the per-entry metadata needed
/// for matching, and the face is parsed on demand by <c>FontCache</c> when a query
/// resolves to a specific entry. This bounds memory use even when the host has hundreds
/// of system fonts installed.
/// </para>
/// <para>
/// Phase 1 covers the four platforms NetPdf targets (macOS, Windows, Linux, Alpine).
/// <b>TTC / OTC collection files are scanned but currently NOT indexed</b>: the
/// <see cref="OpenTypeFont.Parse"/> entry point doesn't yet support collection
/// containers, so <see cref="TryIndex"/> swallows the parse failure and skips the
/// file. Full multi-face collection support — including indexing face 0 (and
/// optionally subsequent faces) of every <c>.ttc</c> / <c>.otc</c> reachable on disk
/// — lands when the collection parser does (post-Phase-1).
/// </para>
/// </remarks>
internal abstract class SystemFontEnumerator
{
    /// <summary>Standard system font directories for the platform. Missing directories are skipped silently.</summary>
    protected abstract IEnumerable<string> FontDirectories { get; }

    /// <summary>Whether to recurse into subdirectories of <see cref="FontDirectories"/>.</summary>
    protected virtual bool Recurse => true;

    /// <summary>
    /// Walk every configured directory and yield one <see cref="SystemFontEntry"/> per
    /// successfully-parsed font file. Files that fail to open or parse are skipped silently
    /// — a corrupt font on disk should not break enumeration of every other font.
    /// </summary>
    public IEnumerable<SystemFontEntry> Enumerate()
    {
        foreach (var dir in FontDirectories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try
            {
                files = EnumerateFontFiles(dir);
            }
            catch (UnauthorizedAccessException)
            {
                // Permission-restricted directories (e.g. /Library/Fonts owned by root with
                // no group-read on some Linux distros) are skipped silently.
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (TryIndex(file, out var entry)) yield return entry;
            }
        }
    }

    private IEnumerable<string> EnumerateFontFiles(string dir)
    {
        var option = Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        // Multi-extension match. We hit each extension separately so a transient failure on
        // one pattern (e.g. permissions on a single sub-tree) does not stop the others.
        foreach (var ext in new[] { "*.ttf", "*.otf", "*.ttc", "*.otc" })
        {
            string[] paths;
            try
            {
                paths = Directory.GetFiles(dir, ext, option);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var p in paths) yield return p;
        }
    }

    private static bool TryIndex(string filePath, out SystemFontEntry entry)
    {
        entry = default;
        try
        {
            // Read once, parse OpenType. For TTC / OTC the parse currently fails (Phase 1
            // does not support collection parsing) — caller swallows the error and skips.
            var bytes = File.ReadAllBytes(filePath);
            var font = OpenTypeFont.Parse(bytes);
            var meta = FontMetadata.Extract(font);
            entry = new SystemFontEntry
            {
                FilePath = filePath,
                FaceIndex = 0,
                FamilyName = meta.FamilyName,
                SubfamilyName = meta.SubfamilyName,
                PostScriptName = meta.PostScriptName,
                WeightCss = meta.WeightCss,
                StretchCss = meta.StretchCss,
                IsItalic = meta.IsItalic || meta.IsOblique,
            };
            return !string.IsNullOrEmpty(entry.FamilyName);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (InvalidDataException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Construct the appropriate enumerator for the current OS. Falls back to the Linux
    /// enumerator on any platform NetPdf has not specifically targeted; the typical Linux
    /// directories are the de-facto convention on most Unix-likes (FreeBSD, illumos).
    /// </summary>
    public static SystemFontEnumerator CreateForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacOsSystemFontEnumerator();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsSystemFontEnumerator();
        return new LinuxSystemFontEnumerator();
    }
}
