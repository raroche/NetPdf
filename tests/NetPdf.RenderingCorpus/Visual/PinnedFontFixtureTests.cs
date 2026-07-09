// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO;
using Xunit;

namespace NetPdf.RenderingCorpus.Visual;

/// <summary>
/// Guards the committed pinned-font pack (<see cref="VisualHarness.FontsDir"/>). PR #309 committed the DejaVu
/// Sans RIBBI faces so the visual-regression diff is font-deterministic — but a font "pinned" as a GitHub
/// blob-HTML page instead of the real <c>sfnt</c> binary silently defeats the whole gate (the resolver would
/// hand Chrome/NetPdf a <c>&lt;!DOCTYPE html&gt;</c> byte stream, not a font). These tests assert every
/// committed <c>.ttf</c> is a real OpenType/TrueType file so that failure mode can never ship undetected.
/// </summary>
public sealed class PinnedFontFixtureTests
{
    // The RIBBI faces PinnedFontResolver selects between; all four must be present and valid.
    public static readonly string[] ExpectedFaces =
    [
        "DejaVuSans.ttf",
        "DejaVuSans-Bold.ttf",
        "DejaVuSans-Oblique.ttf",
        "DejaVuSans-BoldOblique.ttf",
    ];

    public static TheoryData<string> AllCommittedTtfFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(VisualHarness.FontsDir, "*.ttf"))
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Fact]
    public void All_four_RIBBI_faces_are_committed()
    {
        foreach (var face in ExpectedFaces)
        {
            var path = Path.Combine(VisualHarness.FontsDir, face);
            Assert.True(File.Exists(path), $"pinned font fixture missing: {face}");
        }
    }

    [Theory]
    [MemberData(nameof(AllCommittedTtfFiles))]
    public void Committed_ttf_is_a_real_sfnt_not_html(string fileName)
    {
        var path = Path.Combine(VisualHarness.FontsDir, fileName);
        Span<byte> head = stackalloc byte[4];
        using (var fs = File.OpenRead(path))
        {
            var read = fs.Read(head);
            Assert.True(read == 4, $"{fileName} is smaller than a font header ({read} bytes)");
        }

        // Valid sfnt wrappers: 0x00010000 (TrueType), 'OTTO' (CFF/OpenType), 'ttcf' (collection),
        // plus the legacy 'true'/'typ1' Apple tags. Anything else — notably '<!DO' / '\n\n\n\n' from a
        // saved HTML page — is not a font and must fail the gate.
        Assert.True(
            IsValidSfntTag(head),
            $"{fileName} is not a valid sfnt font (first 4 bytes: {head[0]:X2} {head[1]:X2} {head[2]:X2} {head[3]:X2}). "
                + "It is likely a downloaded HTML blob page rather than the raw .ttf binary.");
    }

    private static bool IsValidSfntTag(ReadOnlySpan<byte> tag) =>
        (tag[0] == 0x00 && tag[1] == 0x01 && tag[2] == 0x00 && tag[3] == 0x00) // 0x00010000 TrueType outlines
        || tag.SequenceEqual("OTTO"u8)  // OpenType with CFF outlines
        || tag.SequenceEqual("ttcf"u8)  // TrueType/OpenType Collection
        || tag.SequenceEqual("true"u8)  // legacy Apple TrueType
        || tag.SequenceEqual("typ1"u8); // legacy Apple Type 1
}
