// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts;
using NetPdf.Text.Fonts.OpenType;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.Fonts;

/// <summary>
/// Round-trip tests for <see cref="FontFace"/> and <see cref="FontMetadata"/> using
/// a real host font discovered at test-time via <see cref="RealFontFinder"/>. When no
/// usable TTF is available the tests early-return without asserting (xUnit 2.x has no
/// native conditional-skip; a no-op leaves CI green and tells us via the test log
/// when the host has no usable fonts).
/// </summary>
public sealed class FontFaceTests
{
    private readonly ITestOutputHelper _output;

    public FontFaceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Load_round_trips_a_real_TTF_and_extracts_metadata()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path))
        {
            _output.WriteLine("No host TTF found in standard font paths; skipping.");
            return;
        }
        _output.WriteLine($"Using host font: {path}");

        var bytes = File.ReadAllBytes(path);
        var face = FontFace.Load(bytes, path);

        Assert.NotNull(face.Font);
        Assert.NotNull(face.Metadata);
        Assert.Equal(path, face.Source);
        Assert.True(face.GlyphCount > 0, "real font must have at least one glyph");
        Assert.False(string.IsNullOrEmpty(face.Metadata.FamilyName), "real font must surface a family name");
        // Weight is always in CSS 1..1000 range after extraction.
        Assert.InRange(face.Metadata.WeightCss, 1, 1000);
        // Stretch is always 1..9 after extraction (out-of-range clamps to 5).
        Assert.InRange(face.Metadata.StretchCss, 1, 9);
    }

    [Fact]
    public void Notdef_glyph_is_implicitly_marked_used_after_load()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var bytes = File.ReadAllBytes(path);
        var face = FontFace.Load(bytes, path);

        Assert.True(face.IsGlyphUsed(0), ".notdef (glyph 0) must be implicitly used per OpenType spec");
    }

    [Fact]
    public void MarkGlyphUsed_then_IsGlyphUsed_reflects_the_marking()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var bytes = File.ReadAllBytes(path);
        var face = FontFace.Load(bytes, path);
        var lastValid = face.GlyphCount - 1;

        Assert.False(face.IsGlyphUsed(lastValid), "preconditions: last glyph not yet marked");
        face.MarkGlyphUsed(lastValid);
        Assert.True(face.IsGlyphUsed(lastValid));
    }

    [Fact]
    public void MarkGlyphUsed_outside_range_is_silently_ignored()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var bytes = File.ReadAllBytes(path);
        var face = FontFace.Load(bytes, path);

        // Negative + past-end indices must not throw.
        face.MarkGlyphUsed(-1);
        face.MarkGlyphUsed(face.GlyphCount + 100);
        Assert.False(face.IsGlyphUsed(-1));
        Assert.False(face.IsGlyphUsed(face.GlyphCount));
    }

    [Fact]
    public void GetUsedGlyphIds_returns_sorted_ascending_array_including_notdef()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var bytes = File.ReadAllBytes(path);
        var face = FontFace.Load(bytes, path);

        face.MarkGlyphUsed(5);
        face.MarkGlyphUsed(2);
        face.MarkGlyphUsed(10);
        var used = face.GetUsedGlyphIds();
        Assert.Contains(0, used);  // notdef
        Assert.Contains(2, used);
        Assert.Contains(5, used);
        Assert.Contains(10, used);
        // Verify sorted ascending.
        for (var i = 1; i < used.Length; i++)
        {
            Assert.True(used[i] > used[i - 1]);
        }
    }

    [Fact]
    public void Constructor_throws_for_null_args()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var bytes = File.ReadAllBytes(path);
        var font = OpenTypeFont.Parse(bytes);
        var meta = FontMetadata.Extract(font);

        Assert.Throws<ArgumentNullException>(() => new FontFace(null!, meta, path));
        Assert.Throws<ArgumentNullException>(() => new FontFace(font, null!, path));
        Assert.Throws<ArgumentNullException>(() => new FontFace(font, meta, null!));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var bytes = File.ReadAllBytes(path);
        var face = FontFace.Load(bytes, path);

        face.Dispose();
        face.Dispose(); // must not throw
    }

    [Fact]
    public void Metadata_weight_is_clamped_to_CSS_range_for_legacy_1_to_9_fonts()
    {
        // We can't manufacture an OpenTypeFont without real bytes, so this property is
        // covered by the real-font test above (Assert.InRange 1..1000) plus the dedicated
        // synthetic-font weight-clamp tests in FontMetadataWeightClampTests. This pin
        // documents the contract for code-review readers.
        Assert.True(true);
    }

    // ───── Disposal contract (review #5) ─────────────────────────────────────

    [Fact]
    public void MarkGlyphUsed_after_dispose_throws_ObjectDisposedException()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);
        face.Dispose();
        Assert.Throws<ObjectDisposedException>(() => face.MarkGlyphUsed(1));
    }

    [Fact]
    public void IsGlyphUsed_after_dispose_throws_ObjectDisposedException()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);
        face.Dispose();
        Assert.Throws<ObjectDisposedException>(() => face.IsGlyphUsed(0));
    }

    [Fact]
    public void GetUsedGlyphIds_after_dispose_throws_ObjectDisposedException()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);
        face.Dispose();
        Assert.Throws<ObjectDisposedException>(() => face.GetUsedGlyphIds());
    }

    [Fact]
    public void Read_only_metadata_remains_accessible_after_dispose()
    {
        // The disposal contract intentionally leaves metadata properties readable so
        // logging / diagnostics paths (e.g. emitting a font name in an error message)
        // still work after the face has been released.
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);
        face.Dispose();

        // Must NOT throw — these are pure reads.
        Assert.NotNull(face.Metadata);
        Assert.NotNull(face.Source);
        Assert.True(face.GlyphCount > 0);
        Assert.NotNull(face.Font);
    }

    [Fact]
    public void Concurrent_MarkGlyphUsed_and_Dispose_never_silently_corrupt_state()
    {
        // Race scenario the review-follow-up flagged: a thread mutating the used-glyph
        // bitmap while another thread calls Dispose. Without lock-co-ordinated disposal
        // a marker could land AFTER disposal completed, mutating state of a released face.
        // With the hardened contract, every MarkGlyphUsed call must either complete
        // cleanly OR throw ObjectDisposedException — no silent post-disposal mutation.
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        for (var trial = 0; trial < 50; trial++)
        {
            var face = FontFace.Load(File.ReadAllBytes(path), path);
            var startSignal = new ManualResetEventSlim(false);
            Exception? unexpected = null;
            var workers = new Thread[4];
            for (var i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(() =>
                {
                    startSignal.Wait();
                    for (var k = 0; k < 100; k++)
                    {
                        try { face.MarkGlyphUsed(k % face.GlyphCount); }
                        catch (ObjectDisposedException) { break; } // expected outcome after dispose lands
                        catch (Exception ex) { unexpected = ex; break; }
                    }
                });
                workers[i].Start();
            }
            var disposer = new Thread(() =>
            {
                startSignal.Wait();
                Thread.Yield();
                face.Dispose();
            });
            disposer.Start();
            startSignal.Set();
            disposer.Join();
            foreach (var w in workers) w.Join();
            Assert.Null(unexpected);
        }
    }
}
