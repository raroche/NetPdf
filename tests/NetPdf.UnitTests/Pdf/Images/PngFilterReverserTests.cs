// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Images;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Images;

/// <summary>
/// Direct unit tests for <see cref="PngFilterReverser"/>. Exercises each filter type
/// (None / Sub / Up / Average / Paeth) on hand-crafted byte sequences with known
/// expected outputs. The Paeth tests pin the predictor's exact tie-breaking rule.
/// </summary>
public sealed class PngFilterReverserTests
{
    [Fact]
    public void None_filter_passes_bytes_through_unchanged()
    {
        // 1 row, 4 bytes wide, filter type 0 (None).
        var input = new byte[] { 0, 10, 20, 30, 40 }; // [filter, ...4 data bytes]
        var output = PngFilterReverser.Reverse(input, height: 1, scanlineByteWidth: 4, bytesPerPixel: 1);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, output);
    }

    [Fact]
    public void Sub_filter_adds_left_neighbor()
    {
        // Sub: Recon(x) = Filt(x) + Recon(a)  where 'a' is left at distance bpp.
        // Encode original [10, 20, 30, 40] with bpp=1:
        //   Filt[0] = 10 - 0 = 10
        //   Filt[1] = 20 - 10 = 10
        //   Filt[2] = 30 - 20 = 10
        //   Filt[3] = 40 - 30 = 10
        // Filtered row: [10, 10, 10, 10] with filter byte 1 (Sub).
        var input = new byte[] { 1, 10, 10, 10, 10 };
        var output = PngFilterReverser.Reverse(input, 1, 4, 1);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, output);
    }

    [Fact]
    public void Up_filter_adds_above_byte()
    {
        // Up: Recon(x) = Filt(x) + Recon(b)  where 'b' is the byte above.
        // 2 rows, 4 bytes each:
        //   row 0 (filter None): [50, 60, 70, 80]
        //   row 1 (filter Up):   each byte = original - row0_above
        // Original row 1: [55, 65, 75, 85]. Filt: [55-50, 65-60, 75-70, 85-80] = [5, 5, 5, 5].
        var input = new byte[] {
            0, 50, 60, 70, 80,    // filter 0 (None)
            2, 5, 5, 5, 5,        // filter 2 (Up)
        };
        var output = PngFilterReverser.Reverse(input, 2, 4, 1);
        Assert.Equal(new byte[] { 50, 60, 70, 80, 55, 65, 75, 85 }, output);
    }

    [Fact]
    public void Average_filter_uses_floor_of_left_plus_above_divided_by_2()
    {
        // Average: Recon(x) = Filt(x) + floor((Recon(a) + Recon(b)) / 2).
        // Row 0 (None): [0, 0, 0, 0]
        // Row 1 (Average): want decoded [10, 30, 50, 70].
        //   left=0, up=0  → floor((0+0)/2) = 0  → Filt = 10 - 0 = 10
        //   left=10, up=0 → floor((10+0)/2) = 5 → Filt = 30 - 5 = 25
        //   left=30, up=0 → floor((30+0)/2) = 15 → Filt = 50 - 15 = 35
        //   left=50, up=0 → floor((50+0)/2) = 25 → Filt = 70 - 25 = 45
        var input = new byte[] {
            0, 0, 0, 0, 0,
            3, 10, 25, 35, 45,
        };
        var output = PngFilterReverser.Reverse(input, 2, 4, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 10, 30, 50, 70 }, output);
    }

    [Fact]
    public void Paeth_predictor_picks_a_when_pa_lowest()
    {
        // Paeth: Recon(x) = Filt(x) + PaethPredictor(a, b, c).
        // For a=10, b=10, c=10: p = 10+10-10 = 10. pa = 0, pb = 0, pc = 0.
        // Tie-breaking: pa <= pb && pa <= pc → return a = 10.
        // Row 0 (None): [10, 10] (left and previous-row left = 10, plus above)
        // Row 1 (Paeth) at column 1: a=10 (left), b=10 (above), c=10 (above-left).
        // For decoded value 30: Filt = 30 - 10 = 20.
        var input = new byte[] {
            0, 10, 10,
            4, 0, 20, // first byte: a=0, b=10, c=0. paeth predicts... left tied with above.
        };
        // With a=0, b=10, c=0: p = 0+10-0 = 10. pa = |10-0|=10, pb = |10-10|=0, pc = |10-0|=10.
        // pa > pb so first condition fails. pb <= pc → return b = 10. So Recon = 0 + 10 = 10.
        // Then column 1: a=10 (left, just decoded), b=10 (above), c=10 (above-left).
        // p=10, pa=0, pb=0, pc=0 → pa<=pb && pa<=pc → return a=10. Recon = 20 + 10 = 30.
        var output = PngFilterReverser.Reverse(input, 2, 2, 1);
        Assert.Equal(new byte[] { 10, 10, 10, 30 }, output);
    }

    [Fact]
    public void Reverse_throws_for_unknown_filter_type()
    {
        var input = new byte[] { 99, 0, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() =>
            PngFilterReverser.Reverse(input, 1, 4, 1));
    }

    [Fact]
    public void Reverse_throws_for_size_mismatch()
    {
        var input = new byte[] { 0, 1, 2, 3 }; // 1 byte short
        Assert.Throws<InvalidDataException>(() =>
            PngFilterReverser.Reverse(input, 1, 4, 1));
    }

    [Fact]
    public void Reverse_handles_bpp_greater_than_one_for_Sub_filter()
    {
        // RGB image, 2 pixels = 6 bytes per row, bpp=3.
        // Decoded: [10, 20, 30, 40, 50, 60].
        // Sub filter: x[i] - x[i-3] (or 0 if i < 3).
        //   pos 0: 10 - 0 = 10
        //   pos 1: 20 - 0 = 20
        //   pos 2: 30 - 0 = 30
        //   pos 3: 40 - 10 = 30
        //   pos 4: 50 - 20 = 30
        //   pos 5: 60 - 30 = 30
        // Filtered row: [10, 20, 30, 30, 30, 30] with filter byte 1.
        var input = new byte[] { 1, 10, 20, 30, 30, 30, 30 };
        var output = PngFilterReverser.Reverse(input, 1, 6, 3);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, output);
    }
}
