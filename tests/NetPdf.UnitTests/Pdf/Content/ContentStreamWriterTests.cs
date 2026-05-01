// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;
using System.IO.Compression;
using System.Text;
using NetPdf.Pdf;
using NetPdf.Pdf.Content;
using NetPdf.Pdf.Objects;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Content;

public sealed class ContentStreamWriterTests
{
    // --------------------------------------------------- Graphics state operators

    [Fact]
    public void SaveState_emits_q()
    {
        Assert.Equal("q\n", Render(c => { c.SaveState(); c.RestoreState(); }).Substring(0, 2));
    }

    [Fact]
    public void SaveState_RestoreState_balanced_round_trip()
    {
        Assert.Equal("q\nQ\n", Render(c => { c.SaveState(); c.RestoreState(); }));
    }

    [Fact]
    public void Nested_save_restore_balanced()
    {
        var s = Render(c =>
        {
            c.SaveState();
            c.SaveState();
            c.RestoreState();
            c.RestoreState();
        });
        Assert.Equal("q\nq\nQ\nQ\n", s);
    }

    [Fact]
    public void RestoreState_without_matching_SaveState_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.RestoreState()));
    }

    [Fact]
    public void Finish_throws_when_save_depth_unbalanced()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.SaveState()));
        Assert.Contains("unmatched 'q'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcatMatrix_emits_six_operands_then_cm()
    {
        // Identity + Y-flip for a Letter page (792 pt tall): "1 0 0 -1 0 792 cm".
        var s = Render(c => c.ConcatMatrix(1, 0, 0, -1, 0, 792));
        Assert.Equal("1 0 0 -1 0 792 cm\n", s);
    }

    // --------------------------------------------------- Path construction

    [Fact]
    public void MoveTo_LineTo_emits_m_l()
    {
        var s = Render(c => { c.MoveTo(10, 20); c.LineTo(30, 40); });
        Assert.Equal("10 20 m\n30 40 l\n", s);
    }

    [Fact]
    public void Rectangle_emits_re()
    {
        var s = Render(c => c.Rectangle(0, 0, 100, 50));
        Assert.Equal("0 0 100 50 re\n", s);
    }

    [Fact]
    public void ClosePath_emits_h()
    {
        var s = Render(c => c.ClosePath());
        Assert.Equal("h\n", s);
    }

    // --------------------------------------------------- Path painting

    [Theory]
    [InlineData(nameof(ContentStreamWriter.Fill), "f")]
    [InlineData(nameof(ContentStreamWriter.Stroke), "S")]
    [InlineData(nameof(ContentStreamWriter.EndPath), "n")]
    public void Path_painting_operators_emit_expected_token(string method, string op)
    {
        var s = Render(c =>
        {
            switch (method)
            {
                case nameof(ContentStreamWriter.Fill): c.Fill(); break;
                case nameof(ContentStreamWriter.Stroke): c.Stroke(); break;
                case nameof(ContentStreamWriter.EndPath): c.EndPath(); break;
            }
        });
        Assert.Equal($"{op}\n", s);
    }

    [Fact]
    public void Filled_rectangle_round_trip()
    {
        var s = Render(c =>
        {
            c.Rectangle(100, 100, 50, 50);
            c.Fill();
        });
        Assert.Equal("100 100 50 50 re\nf\n", s);
    }

    // --------------------------------------------------- Color

    [Fact]
    public void SetFillRgb_emits_rg()
    {
        var s = Render(c => c.SetFillRgb(0.5, 0.25, 0.75));
        Assert.Equal("0.5 0.25 0.75 rg\n", s);
    }

    [Fact]
    public void SetStrokeRgb_emits_RG()
    {
        var s = Render(c => c.SetStrokeRgb(1, 0, 0));
        Assert.Equal("1 0 0 RG\n", s);
    }

    [Fact]
    public void SetFillGray_emits_g()
    {
        var s = Render(c => c.SetFillGray(0.5));
        Assert.Equal("0.5 g\n", s);
    }

    [Fact]
    public void SetStrokeGray_emits_G()
    {
        var s = Render(c => c.SetStrokeGray(0));
        Assert.Equal("0 G\n", s);
    }

    // --------------------------------------------------- Line width

    [Fact]
    public void SetLineWidth_emits_w()
    {
        var s = Render(c => c.SetLineWidth(2.5));
        Assert.Equal("2.5 w\n", s);
    }

    [Fact]
    public void SetLineWidth_negative_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Render(c => c.SetLineWidth(-1)));
    }

    // --------------------------------------------------- Text

    [Fact]
    public void BeginText_EndText_balanced()
    {
        var s = Render(c => { c.BeginText(); c.EndText(); });
        Assert.Equal("BT\nET\n", s);
    }

    [Fact]
    public void BeginText_inside_text_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => { c.BeginText(); c.BeginText(); }));
    }

    [Fact]
    public void EndText_outside_text_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.EndText()));
    }

    [Fact]
    public void Finish_throws_when_inside_text_object()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.BeginText()));
        Assert.Contains("BT/ET", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetFont_emits_name_size_Tf()
    {
        var s = Render(c =>
        {
            c.BeginText();
            c.SetFont(new PdfName("F1"), 12);
            c.EndText();
        });
        Assert.Equal("BT\n/F1 12 Tf\nET\n", s);
    }

    [Fact]
    public void SetFont_outside_text_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.SetFont(new PdfName("F1"), 12)));
    }

    [Fact]
    public void SetFont_zero_or_negative_size_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Render(c => { c.BeginText(); c.SetFont(new PdfName("F1"), 0); c.EndText(); }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Render(c => { c.BeginText(); c.SetFont(new PdfName("F1"), -1); c.EndText(); }));
    }

    [Fact]
    public void MoveTextPosition_emits_Td()
    {
        var s = Render(c => { c.BeginText(); c.MoveTextPosition(100, 700); c.EndText(); });
        Assert.Equal("BT\n100 700 Td\nET\n", s);
    }

    [Fact]
    public void MoveTextPositionAndSetLeading_emits_TD()
    {
        var s = Render(c => { c.BeginText(); c.MoveTextPositionAndSetLeading(0, -14); c.EndText(); });
        Assert.Equal("BT\n0 -14 TD\nET\n", s);
    }

    [Fact]
    public void ShowText_emits_paren_string_Tj()
    {
        var bytes = Encoding.ASCII.GetBytes("Hello");
        var s = Render(c => { c.BeginText(); c.ShowText(bytes); c.EndText(); });
        Assert.Equal("BT\n(Hello) Tj\nET\n", s);
    }

    [Fact]
    public void ShowText_escapes_parens_and_backslash()
    {
        var bytes = Encoding.ASCII.GetBytes("a(b)c\\d");
        var s = Render(c => { c.BeginText(); c.ShowText(bytes); c.EndText(); });
        Assert.Equal("BT\n(a\\(b\\)c\\\\d) Tj\nET\n", s);
    }

    [Fact]
    public void ShowTextArray_emits_TJ_with_strings_and_offsets()
    {
        var s = Render(c =>
        {
            c.BeginText();
            c.ShowTextArray(new[]
            {
                TextArrayElement.String(Encoding.ASCII.GetBytes("Hello")),
                TextArrayElement.Adjust(-100),
                TextArrayElement.String(Encoding.ASCII.GetBytes("World")),
            });
            c.EndText();
        });
        Assert.Equal("BT\n[(Hello) -100 (World)] TJ\nET\n", s);
    }

    [Fact]
    public void Text_operator_outside_text_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.ShowText("Hi"u8)));
    }

    [Fact]
    public void Path_operator_inside_text_throws()
    {
        // PDF spec: most path operators are not valid inside a BT/ET text object.
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => { c.BeginText(); c.Rectangle(0, 0, 10, 10); c.EndText(); }));
    }

    // --------------------------------------------------- XObject

    [Fact]
    public void PaintXObject_emits_name_Do()
    {
        var s = Render(c => c.PaintXObject(new PdfName("Im1")));
        Assert.Equal("/Im1\nDo\n", s);
    }

    [Fact]
    public void PaintXObject_inside_text_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => { c.BeginText(); c.PaintXObject(new PdfName("Im1")); c.EndText(); }));
    }

    // --------------------------------------------------- Marked content

    [Fact]
    public void BeginMarkedContent_EndMarkedContent_balanced()
    {
        var s = Render(c =>
        {
            c.BeginMarkedContent(new PdfName("Span"));
            c.EndMarkedContent();
        });
        Assert.Equal("/Span\nBMC\nEMC\n", s);
    }

    [Fact]
    public void BeginMarkedContentWithProperties_emits_BDC()
    {
        var props = new PdfDictionary().Set(PdfNames.MCID, new PdfInteger(0));
        var s = Render(c =>
        {
            c.BeginMarkedContentWithProperties(new PdfName("Span"), props);
            c.EndMarkedContent();
        });
        Assert.Equal("/Span << /MCID 0 >>\nBDC\nEMC\n", s);
    }

    [Fact]
    public void EndMarkedContent_without_matching_begin_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.EndMarkedContent()));
    }

    [Fact]
    public void Finish_throws_on_unbalanced_marked_content()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Render(c => c.BeginMarkedContent(new PdfName("Span"))));
        Assert.Contains("marked-content", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------- Finish semantics

    [Fact]
    public void Operations_after_Finish_throw()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buffer);
        var c = new ContentStreamWriter(w);
        c.Finish();

        Assert.Throws<InvalidOperationException>(() => c.SaveState());
    }

    [Fact]
    public void Finish_called_twice_throws()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buffer);
        var c = new ContentStreamWriter(w);
        c.Finish();

        Assert.Throws<InvalidOperationException>(() => c.Finish());
    }

    // --------------------------------------------------- Determinism

    [Fact]
    public void Determinism_byte_equal_for_byte_equal_input()
    {
        var first = Render(BuildSampleStream);
        var second = Render(BuildSampleStream);
        Assert.Equal(first, second);
    }

    private static void BuildSampleStream(ContentStreamWriter c)
    {
        c.SaveState();
        c.ConcatMatrix(1, 0, 0, -1, 0, 792);
        c.SetFillRgb(0.5, 0.5, 0.5);
        c.Rectangle(100, 100, 200, 50);
        c.Fill();
        c.RestoreState();

        c.BeginText();
        c.SetFont(new PdfName("F1"), 12);
        c.MoveTextPosition(100, 100);
        c.ShowText(Encoding.ASCII.GetBytes("Hello, world."));
        c.EndText();
    }

    // ------------------------------------------------------------------------------------

    private static string Render(Action<ContentStreamWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PdfWriter(buffer);
        var c = new ContentStreamWriter(w);
        body(c);
        c.Finish();
        return Encoding.Latin1.GetString(buffer.WrittenSpan);
    }
}

public sealed class ContentStreamBuilderTests
{
    [Fact]
    public void Build_returns_pdfstream_with_payload()
    {
        var stream = ContentStreamBuilder.Build(c =>
        {
            c.Rectangle(0, 0, 100, 100);
            c.Fill();
        });

        Assert.True(stream.Data.Length > 0);
        var ascii = Encoding.Latin1.GetString(stream.Data);
        Assert.Equal("0 0 100 100 re\nf\n", ascii);
    }

    [Fact]
    public void Build_compressed_emits_filter_and_decompresses_to_original()
    {
        var stream = ContentStreamBuilder.Build(c =>
        {
            c.Rectangle(0, 0, 100, 100);
            c.Fill();
        }, compress: true);

        Assert.True(stream.Dictionary.Get(PdfNames.Filter) is PdfName filterName
            && filterName.Equals(PdfNames.FlateDecode));

        // Round-trip: decompress the payload and verify it matches the uncompressed source.
        using var input = new MemoryStream(stream.Data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        Assert.Equal("0 0 100 100 re\nf\n", Encoding.Latin1.GetString(output.ToArray()));
    }

    [Fact]
    public void Build_compressed_payload_is_smaller_for_repetitive_content()
    {
        // Large-ish repetitive content compresses well; gives confidence the filter actually fires.
        var raw = ContentStreamBuilder.Build(c =>
        {
            for (int i = 0; i < 200; i++)
            {
                c.Rectangle(0, 0, 10, 10);
                c.Fill();
            }
        }, compress: false);

        var compressed = ContentStreamBuilder.Build(c =>
        {
            for (int i = 0; i < 200; i++)
            {
                c.Rectangle(0, 0, 10, 10);
                c.Fill();
            }
        }, compress: true);

        Assert.True(compressed.Data.Length < raw.Data.Length);
    }

    [Fact]
    public void Build_calls_Finish_so_unbalanced_state_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ContentStreamBuilder.Build(c => c.SaveState()));
    }

    [Fact]
    public void Build_null_body_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ContentStreamBuilder.Build(null!));
    }
}
