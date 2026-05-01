// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Paint;
using Xunit;

namespace NetPdf.UnitTests.Paint;

public sealed class DisplayListTests
{
    [Fact]
    public void Newly_created_list_is_empty()
    {
        using var list = new DisplayList();
        Assert.Equal(0, list.Count);
        Assert.True(list.Commands.IsEmpty);
        Assert.Empty(list.TextRuns);
        Assert.Empty(list.Images);
    }

    [Fact]
    public void Add_appends_in_insertion_order()
    {
        using var list = new DisplayList();
        list.Add(DisplayCommand.RectFill(0, 0, 10, 10, RgbaColor.Black));
        list.Add(DisplayCommand.OpacityPush(0.5));
        list.Add(DisplayCommand.OpacityPop());

        Assert.Equal(3, list.Count);
        Assert.Equal(DisplayCommandKind.RectFill, list.Commands[0].Kind);
        Assert.Equal(DisplayCommandKind.OpacityPush, list.Commands[1].Kind);
        Assert.Equal(DisplayCommandKind.OpacityPop, list.Commands[2].Kind);
    }

    [Fact]
    public void Add_grows_buffer_past_initial_capacity()
    {
        using var list = new DisplayList();
        const int n = 1000; // well past InitialCapacity (64), forces multiple grows
        for (int i = 0; i < n; i++)
        {
            list.Add(DisplayCommand.RectFill(i, i, 1, 1, new RgbaColor((byte)(i & 0xFF), 0, 0)));
        }
        Assert.Equal(n, list.Count);
        for (int i = 0; i < n; i++)
        {
            var p = list.Commands[i].AsRectFill();
            Assert.Equal(i, p.X);
            Assert.Equal(i, p.Y);
            Assert.Equal((byte)(i & 0xFF), p.Color.R);
        }
    }

    [Fact]
    public void AddTextRun_returns_sequential_indices_starting_at_zero()
    {
        using var list = new DisplayList();
        var t0 = new TextRun { FontId = -1, FontSize = 12, Color = RgbaColor.Black, Text = "a" };
        var t1 = new TextRun { FontId = -1, FontSize = 12, Color = RgbaColor.Black, Text = "b" };
        var t2 = new TextRun { FontId = -1, FontSize = 12, Color = RgbaColor.Black, Text = "c" };

        Assert.Equal(0, list.AddTextRun(t0));
        Assert.Equal(1, list.AddTextRun(t1));
        Assert.Equal(2, list.AddTextRun(t2));

        Assert.Equal(3, list.TextRuns.Count);
        Assert.Same(t0, list.TextRuns[0]);
        Assert.Same(t1, list.TextRuns[1]);
        Assert.Same(t2, list.TextRuns[2]);
    }

    [Fact]
    public void AddImage_returns_sequential_indices_starting_at_zero()
    {
        using var list = new DisplayList();
        var i0 = SamplePng();
        var i1 = SamplePng();

        Assert.Equal(0, list.AddImage(i0));
        Assert.Equal(1, list.AddImage(i1));

        Assert.Equal(2, list.Images.Count);
        Assert.Same(i0, list.Images[0]);
        Assert.Same(i1, list.Images[1]);
    }

    [Fact]
    public void GetTextRun_resolves_by_index()
    {
        using var list = new DisplayList();
        var t = new TextRun { FontId = 0, FontSize = 14, Color = RgbaColor.Black, Text = "x" };
        var idx = list.AddTextRun(t);
        Assert.Same(t, list.GetTextRun(idx));
    }

    [Fact]
    public void GetTextRun_throws_for_out_of_range_index()
    {
        using var list = new DisplayList();
        Assert.Throws<ArgumentOutOfRangeException>(() => list.GetTextRun(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.GetTextRun(-1));

        list.AddTextRun(new TextRun { FontId = 0, FontSize = 12, Color = RgbaColor.Black, Text = "a" });
        Assert.Throws<ArgumentOutOfRangeException>(() => list.GetTextRun(1));
    }

    [Fact]
    public void GetImage_resolves_by_index()
    {
        using var list = new DisplayList();
        var img = SamplePng();
        var idx = list.AddImage(img);
        Assert.Same(img, list.GetImage(idx));
    }

    [Fact]
    public void GetImage_throws_for_out_of_range_index()
    {
        using var list = new DisplayList();
        Assert.Throws<ArgumentOutOfRangeException>(() => list.GetImage(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => list.GetImage(-1));
    }

    [Fact]
    public void AddTextRun_rejects_null()
    {
        using var list = new DisplayList();
        Assert.Throws<ArgumentNullException>(() => list.AddTextRun(null!));
    }

    [Fact]
    public void AddImage_rejects_null()
    {
        using var list = new DisplayList();
        Assert.Throws<ArgumentNullException>(() => list.AddImage(null!));
    }

    // ───── Determinism ────────────────────────────────────────────────────────

    [Fact]
    public void Identical_build_sequences_produce_value_equal_command_streams()
    {
        using var a = Build();
        using var b = Build();

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a.Commands[i], b.Commands[i]);
        }

        static DisplayList Build()
        {
            var l = new DisplayList();
            l.Add(DisplayCommand.TransformPush(1, 0, 0, 1, 50, 100));
            l.Add(DisplayCommand.OpacityPush(0.5));
            l.Add(DisplayCommand.RectFill(0, 0, 200, 50, new RgbaColor(0xFF, 0, 0)));
            int t = l.AddTextRun(new TextRun { FontId = 0, FontSize = 14, Color = RgbaColor.Black, Text = "Hello" });
            l.Add(DisplayCommand.TextRun(t, 10, 30));
            l.Add(DisplayCommand.OpacityPop());
            l.Add(DisplayCommand.TransformPop());
            return l;
        }
    }

    // ───── Disposal ───────────────────────────────────────────────────────────

    [Fact]
    public void After_Dispose_member_access_throws_ObjectDisposed()
    {
        var list = new DisplayList();
        list.Add(DisplayCommand.OpacityPop());
        list.Dispose();

        Assert.Throws<ObjectDisposedException>(() => list.Count);
        Assert.Throws<ObjectDisposedException>(() => { _ = list.Commands; });
        Assert.Throws<ObjectDisposedException>(() => list.Add(DisplayCommand.OpacityPop()));
        Assert.Throws<ObjectDisposedException>(() => list.AddTextRun(new TextRun
        {
            FontId = 0, FontSize = 12, Color = RgbaColor.Black, Text = "x"
        }));
        Assert.Throws<ObjectDisposedException>(() => list.AddImage(SamplePng()));
        Assert.Throws<ObjectDisposedException>(() => list.GetTextRun(0));
        Assert.Throws<ObjectDisposedException>(() => list.GetImage(0));
        Assert.Throws<ObjectDisposedException>(() => list.TextRuns);
        Assert.Throws<ObjectDisposedException>(() => list.Images);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var list = new DisplayList();
        list.Dispose();
        list.Dispose(); // must not throw
    }

    // ───── helpers ────────────────────────────────────────────────────────────

    private static RasterImage SamplePng() => new()
    {
        EncodedBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }, // not a real PNG; just bytes for the side table
        Encoding = ImageEncoding.Png,
        PixelWidth = 1,
        PixelHeight = 1,
        HasAlpha = false,
    };
}
