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
                if (IsCollectionFile(file))
                {
                    foreach (var face in IndexCollection(file)) yield return face;
                }
                else if (TryIndex(file, out var entry))
                {
                    yield return entry;
                }
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
            // Read once, parse OpenType. Collections (.ttc / .otc) take IndexCollection instead.
            var bytes = File.ReadAllBytes(filePath);
            // Per PR #17 review user-recommendation #4 — system fonts are
            // still an input boundary (user-installed; macOS lets any app
            // drop a font into ~/Library/Fonts). Run the same C-2
            // pre-decode safety gate that FontFace.Load uses, so this
            // production path doesn't bypass the validator. Caller's
            // try/catch already swallows + skips, so a rejected font
            // simply doesn't appear in the index.
            var verdict = FontSafetyValidator.Validate(bytes);
            if (!verdict.IsSafe) return false;
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

    /// <summary>True when the file starts with the <c>ttcf</c> collection tag (the extension alone is not
    /// trusted: some <c>.ttc</c> files hold one plain sfnt, and some <c>.ttf</c> files are collections).</summary>
    private static bool IsCollectionFile(string filePath)
    {
        try
        {
            using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> tag = stackalloc byte[4];
            return RandomAccess.Read(handle, tag, 0) == 4 && FontCollection.IsCollection(tag);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// One entry per usable face of a font collection (<c>.ttc</c> / <c>.otc</c>). Reads only the
    /// collection header, each face's table directory, and its <c>name</c> / <c>OS/2</c> / <c>head</c>
    /// tables — large CJK collections are never read whole. A face is skipped (the others still index)
    /// when its directory is malformed, it lacks a table the full parse requires, it carries a table the
    /// safety validator rejects, or its standalone size is over the validator's byte cap — so a face in
    /// the index is one <see cref="FontCollection.ExtractFace(string, int)"/> can hand to the same
    /// validator and parser a plain <c>.ttf</c> goes through.
    /// </summary>
    internal static List<SystemFontEntry> IndexCollection(string filePath)
    {
        var entries = new List<SystemFontEntry>();
        try
        {
            using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            var source = FontCollection.Source.FromFile(handle);
            var offsets = FontCollection.ReadFaceOffsets(source);
            for (var i = 0; i < offsets.Length; i++)
            {
                if (TryIndexCollectionFace(source, filePath, i, offsets[i], out var entry)) entries.Add(entry);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
        return entries;
    }

    private static bool TryIndexCollectionFace(
        in FontCollection.Source source, string filePath, int faceIndex, uint offset, out SystemFontEntry entry)
    {
        entry = default;
        try
        {
            var face = FontCollection.ReadFaceDirectory(source, offset);
            if (FontCollection.StandaloneSize(face) > FontSafetyValidator.MaxBytes) return false;
            foreach (var t in face.Tables)
            {
                if (FontSafetyValidator.IsDangerousTableTag(t.Tag)) return false;
            }
            foreach (var required in RequiredTables)
            {
                if (!face.TryGet(required, out _)) return false;
            }
            var outlines = face.SfntVersion == OpenTypeTags.SfntVersionOtf
                ? face.TryGet(OpenTypeTags.Cff, out _)
                : face.TryGet(OpenTypeTags.Loca, out _) && face.TryGet(OpenTypeTags.Glyf, out _);
            if (!outlines) return false;

            face.TryGet(OpenTypeTags.Name, out var nameRecord);
            face.TryGet(OpenTypeTags.Os2, out var os2Record);
            face.TryGet(OpenTypeTags.Head, out var headRecord);
            var meta = FontMetadata.FromTables(
                NameTable.Parse(source.ReadTable(nameRecord)),
                Os2Table.Parse(source.ReadTable(os2Record)),
                HeadTable.Parse(source.ReadTable(headRecord)));
            entry = new SystemFontEntry
            {
                FilePath = filePath,
                FaceIndex = faceIndex,
                IsCollectionFace = true,
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
        catch (InvalidDataException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Tables <see cref="OpenTypeFont.Parse"/> requires of every face.</summary>
    private static readonly uint[] RequiredTables =
    [
        OpenTypeTags.Head, OpenTypeTags.Hhea, OpenTypeTags.Maxp, OpenTypeTags.Os2,
        OpenTypeTags.Post, OpenTypeTags.Name, OpenTypeTags.Hmtx, OpenTypeTags.Cmap,
    ];

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
