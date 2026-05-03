// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;
using NetPdf.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

/// <summary>
/// Post-Task-6 hardening: <see cref="NameTable.GetName"/> considers Unicode-platform
/// records (platform 0), not just Windows + Macintosh.
/// </summary>
public sealed class NameTableHardeningTests
{
    [Fact]
    public void GetName_returns_unicode_platform_text_when_no_windows_or_mac_record_exists()
    {
        var name = BuildSingleRecordTable(
            platformId: NameTable.PlatformIdUnicode,
            encodingId: 3,         // Unicode 2.0+ BMP
            languageId: 0,
            nameId: NameTable.NameIdPostScriptName,
            text: "UnicodeOnly-PS");

        Assert.Equal("UnicodeOnly-PS", name.PostScriptName);
    }

    [Fact]
    public void GetName_prefers_windows_over_unicode_when_both_present()
    {
        var name = BuildTwoRecordTable(
            recordA: (NameTable.PlatformIdUnicode, 3, 0, NameTable.NameIdFamilyName, "Unicode-Family"),
            recordB: (NameTable.PlatformIdWindows, 1, 0x0409, NameTable.NameIdFamilyName, "Windows-Family"));

        Assert.Equal("Windows-Family", name.FamilyName);
    }

    [Fact]
    public void GetName_prefers_unicode_over_macintosh_when_no_windows_present()
    {
        var name = BuildTwoRecordTable(
            recordA: (NameTable.PlatformIdMacintosh, 0, 0, NameTable.NameIdFamilyName, "Mac-Family"),
            recordB: (NameTable.PlatformIdUnicode, 3, 0, NameTable.NameIdFamilyName, "Unicode-Family"));

        Assert.Equal("Unicode-Family", name.FamilyName);
    }

    private static NameTable BuildSingleRecordTable(
        ushort platformId,
        ushort encodingId,
        ushort languageId,
        ushort nameId,
        string text)
    {
        var encoded = EncodeForPlatform(platformId, text);
        var headerSize = 6 + 12;
        var bytes = new byte[headerSize + encoded.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);                          // format
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 1);                          // count
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)headerSize);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], platformId);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], encodingId);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], languageId);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], nameId);
        BinaryPrimitives.WriteUInt16BigEndian(span[14..16], (ushort)encoded.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..18], 0);                        // string offset
        encoded.CopyTo(span[headerSize..]);
        return NameTable.Parse(bytes);
    }

    private static NameTable BuildTwoRecordTable(
        (ushort PlatformId, ushort EncodingId, ushort LanguageId, ushort NameId, string Text) recordA,
        (ushort PlatformId, ushort EncodingId, ushort LanguageId, ushort NameId, string Text) recordB)
    {
        var encodedA = EncodeForPlatform(recordA.PlatformId, recordA.Text);
        var encodedB = EncodeForPlatform(recordB.PlatformId, recordB.Text);
        var headerSize = 6 + (12 * 2);
        var bytes = new byte[headerSize + encodedA.Length + encodedB.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span[0..2], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], 2);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)headerSize);

        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], recordA.PlatformId);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], recordA.EncodingId);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], recordA.LanguageId);
        BinaryPrimitives.WriteUInt16BigEndian(span[12..14], recordA.NameId);
        BinaryPrimitives.WriteUInt16BigEndian(span[14..16], (ushort)encodedA.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[16..18], 0);

        BinaryPrimitives.WriteUInt16BigEndian(span[18..20], recordB.PlatformId);
        BinaryPrimitives.WriteUInt16BigEndian(span[20..22], recordB.EncodingId);
        BinaryPrimitives.WriteUInt16BigEndian(span[22..24], recordB.LanguageId);
        BinaryPrimitives.WriteUInt16BigEndian(span[24..26], recordB.NameId);
        BinaryPrimitives.WriteUInt16BigEndian(span[26..28], (ushort)encodedB.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[28..30], (ushort)encodedA.Length);

        encodedA.CopyTo(span[headerSize..]);
        encodedB.CopyTo(span[(headerSize + encodedA.Length)..]);
        return NameTable.Parse(bytes);
    }

    private static byte[] EncodeForPlatform(ushort platformId, string text) => platformId switch
    {
        NameTable.PlatformIdMacintosh => Encoding.ASCII.GetBytes(text),
        _ => Encoding.BigEndianUnicode.GetBytes(text),
    };
}
