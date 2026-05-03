// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO.Compression;

namespace NetPdf.Pdf;

/// <summary>
/// Spec-derived constants tied to ISO 32000-2:2020. Centralized here so the connection
/// between the bytes we emit and the spec sections is explicit, and so any future change
/// (e.g., switching to xref streams) touches one file.
/// </summary>
internal static class PdfFormat
{
    /// <summary>§7.5.4 — byte-offset field in a classic xref entry is 10 digits wide.</summary>
    public const int XrefOffsetDigits = 10;

    /// <summary>§7.5.4 — generation field in a classic xref entry is 5 digits wide.</summary>
    public const int XrefGenerationDigits = 5;

    /// <summary>§7.5.4 — each classic xref entry is exactly 20 bytes including the 2-byte EOL.</summary>
    public const int XrefEntrySize = 20;

    /// <summary>
    /// §7.5.4 — maximum byte offset representable in <see cref="XrefOffsetDigits"/>. Files
    /// larger than this require xref streams (PDF 1.5+) instead of a classic xref table.
    /// </summary>
    public const long MaxXrefByteOffset = 9_999_999_999L;

    /// <summary>§7.5.4 — maximum value of the 5-digit generation field.</summary>
    public const int MaxGeneration = 65535;

    /// <summary>§7.5.4 — generation reserved for the free-list head (object 0).</summary>
    public const int FreeListHeadGeneration = 65535;

    /// <summary>
    /// §7.5.2 — bytes for the binary marker comment (a 4-byte sequence with each byte ≥ 0x80).
    /// Without this comment, transports that handle ASCII (e.g., FTP ASCII mode) might mangle
    /// the file. Specific bytes match Adobe's canonical example.
    /// </summary>
    public static ReadOnlySpan<byte> BinaryMarkerBytes => [0xE2, 0xE3, 0xCF, 0xD3];

    /// <summary>
    /// PDF versions the writer can emit, in stable display order. Used for diagnostic messages
    /// where deterministic ordering matters; for membership checks use <see cref="SupportedVersionSet"/>.
    /// </summary>
    public static readonly string[] SupportedVersions = ["1.4", "1.5", "1.6", "1.7", "2.0"];

    /// <summary>O(1) membership check for <see cref="SupportedVersions"/>.</summary>
    public static readonly IReadOnlySet<string> SupportedVersionSet =
        new HashSet<string>(SupportedVersions, StringComparer.Ordinal);

    /// <summary>
    /// Pinned <see cref="CompressionLevel"/> for every <c>FlateDecode</c> stream NetPdf
    /// emits (image XObjects, content streams, future ToUnicode CMaps, etc). Centralizing
    /// the choice here is part of NetPdf's byte-determinism guarantee — the level the
    /// .NET <c>ZLibStream</c> uses determines the exact output bytes. Two builds that
    /// disagree on the level produce different (still-valid) PDF bytes, which would
    /// silently break any consumer that relies on byte-for-byte stability (signing,
    /// content addressing, snapshot tests). <see cref="CompressionLevel.SmallestSize"/>
    /// is documented as deflate's strongest setting and is the most stable across
    /// runtime versions because the highest-quality slot is conventionally pinned at
    /// deflate level 9.
    /// </summary>
    public const CompressionLevel PdfDeflateCompressionLevel = CompressionLevel.SmallestSize;
}
