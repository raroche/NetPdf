// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// Coverage for the classic xref byte-offset limit (ISO 32000-2:2020 §7.5.4): the
/// 10-digit byte-offset field caps at <see cref="PdfFormat.MaxXrefByteOffset"/>
/// (9,999,999,999 bytes ≈ 9.31 GB). A file that would record an offset past this
/// limit cannot be expressed in a classic xref table — it requires xref streams
/// (PDF 1.5+) which Phase 1 does not emit.
/// <para>
/// Driving the limit through the normal <c>WriteTo</c> path would require building
/// a 10 GB synthetic document; impractical. Instead we validate the guard's
/// boundary directly via the internal
/// <see cref="PdfDocumentWriter.EnsureXrefOffsetFits"/> helper.
/// </para>
/// </summary>
public sealed class PdfXrefOffsetLimitTests
{
    [Fact]
    public void EnsureXrefOffsetFits_accepts_zero_offset()
    {
        // Free-list head is recorded at offset 0; must be permitted.
        PdfDocumentWriter.EnsureXrefOffsetFits(0);
    }

    [Fact]
    public void EnsureXrefOffsetFits_accepts_offset_exactly_at_limit()
    {
        // The largest 10-digit value (9,999,999,999) fits in the 10-digit field
        // exactly — should NOT throw.
        PdfDocumentWriter.EnsureXrefOffsetFits(PdfFormat.MaxXrefByteOffset);
    }

    [Fact]
    public void EnsureXrefOffsetFits_throws_when_offset_exceeds_limit_by_one()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.EnsureXrefOffsetFits(PdfFormat.MaxXrefByteOffset + 1L));
        Assert.Contains("classic xref limit", ex.Message, StringComparison.Ordinal);
        Assert.Contains("xref streams", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureXrefOffsetFits_throws_with_actionable_diagnostic_for_large_overflow()
    {
        // Pretend we tried to record an offset of ~50 GB — confirm the message
        // names the actual offset and points at the xref-stream alternative.
        const long FiftyGigabytes = 50L * 1024 * 1024 * 1024;
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.EnsureXrefOffsetFits(FiftyGigabytes));
        Assert.Contains(FiftyGigabytes.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
        Assert.Contains("EmittedPdfVersion", ex.Message, StringComparison.Ordinal);
    }
}
