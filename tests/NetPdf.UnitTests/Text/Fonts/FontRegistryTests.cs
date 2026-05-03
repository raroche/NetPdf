// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Text.Fonts;

/// <summary>
/// Tests for the per-document <see cref="FontRegistry"/>. Constructs <see cref="FontFace"/>
/// instances over a discovered host TTF; tests early-return without asserting when no
/// usable font is available on the host.
/// </summary>
public sealed class FontRegistryTests
{
    private readonly ITestOutputHelper _output;

    public FontRegistryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Empty_registry_reports_zero_count_and_no_face_lookup()
    {
        using var registry = new FontRegistry();
        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGet("Roboto", weightCss: 400, italic: false, out _));
        Assert.Empty(registry.Faces);
    }

    [Fact]
    public void Register_then_TryGet_returns_the_face()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path))
        {
            _output.WriteLine("No host TTF; skipping.");
            return;
        }
        var face = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, face);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var got));
        Assert.Same(face, got);
    }

    [Fact]
    public void TryGet_is_case_insensitive_on_family_name()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Open Sans", 400, italic: false, face);

        Assert.True(registry.TryGet("open sans", 400, italic: false, out _));
        Assert.True(registry.TryGet("OPEN SANS", 400, italic: false, out _));
    }

    [Fact]
    public void Different_weights_or_italic_are_separate_keys()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face1 = FontFace.Load(File.ReadAllBytes(path), path);
        var face2 = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, face1);
        registry.Register("Roboto", 700, italic: false, face2);

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var got400));
        Assert.True(registry.TryGet("Roboto", 700, italic: false, out var got700));
        Assert.Same(face1, got400);
        Assert.Same(face2, got700);
        Assert.False(registry.TryGet("Roboto", 400, italic: true, out _));
    }

    [Fact]
    public void Re_register_replaces_the_previous_face_without_disposing_it()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face1 = FontFace.Load(File.ReadAllBytes(path), path);
        var face2 = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, face1);
        registry.Register("Roboto", 400, italic: false, face2);

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var got));
        Assert.Same(face2, got);
        // face1 must still be usable — registry doesn't dispose displaced entries.
        Assert.True(face1.GlyphCount > 0);
    }

    [Fact]
    public void Faces_returns_a_snapshot_safe_to_iterate_after_subsequent_registers()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face1 = FontFace.Load(File.ReadAllBytes(path), path);
        var face2 = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, face1);
        var snapshot = registry.Faces;
        Assert.Single(snapshot);

        // Register another after taking the snapshot — snapshot must not be mutated.
        registry.Register("Open Sans", 400, italic: false, face2);
        Assert.Single(snapshot);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Dispose_drops_every_face_and_blocks_further_registers()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);

        var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, face);
        Assert.Equal(1, registry.Count);

        registry.Dispose();
        Assert.Equal(0, registry.Count);
        Assert.Throws<ObjectDisposedException>(() =>
            registry.Register("Roboto", 400, italic: false, face));
    }

    [Fact]
    public void Register_throws_on_null_args()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);
        using var registry = new FontRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!, 400, false, face));
        Assert.Throws<ArgumentNullException>(() => registry.Register("Roboto", 400, false, null!));
    }

    // ───── Stretch in registry key (review #1, #2, #3) ───────────────────────

    [Fact]
    public void Different_stretches_for_same_family_weight_italic_are_separate_keys()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var faceCondensed = FontFace.Load(File.ReadAllBytes(path), path);
        var faceNormal = FontFace.Load(File.ReadAllBytes(path), path);
        var faceExpanded = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, faceCondensed, stretchCss: 3);
        registry.Register("Roboto", 400, italic: false, faceNormal, stretchCss: 5);
        registry.Register("Roboto", 400, italic: false, faceExpanded, stretchCss: 7);

        Assert.Equal(3, registry.Count);
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var got3, stretchCss: 3));
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var got5, stretchCss: 5));
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var got7, stretchCss: 7));
        Assert.Same(faceCondensed, got3);
        Assert.Same(faceNormal, got5);
        Assert.Same(faceExpanded, got7);
    }

    [Fact]
    public void Default_stretch_argument_is_5_normal_width()
    {
        // Backward-compat: omitted stretch must hash + match like an explicit 5.
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, face); // default stretch
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var defaulted));
        Assert.True(registry.TryGet("Roboto", 400, italic: false, out var explicitNormal, stretchCss: 5));
        Assert.Same(defaulted, explicitNormal);
    }

    [Fact]
    public void Mismatched_stretch_lookup_returns_false_even_for_registered_family()
    {
        if (!RealFontFinder.TryFindAnyTtf(out var path)) return;
        var face = FontFace.Load(File.ReadAllBytes(path), path);

        using var registry = new FontRegistry();
        registry.Register("Roboto", 400, italic: false, face, stretchCss: 5);
        Assert.False(registry.TryGet("Roboto", 400, italic: false, out _, stretchCss: 7));
    }
}
