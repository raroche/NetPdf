// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers;

namespace NetPdf.Paint;

/// <summary>
/// Mutable, append-only buffer of <see cref="DisplayCommand"/> values, backed by an
/// <see cref="ArrayPool{T}"/>-rented array, with side tables for variable-length payloads
/// (<see cref="TextRun"/>, <see cref="RasterImage"/>) referenced by <see cref="int"/> index.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="DisplayList"/> per page. The painter (Phase 3) builds these; the PDF
/// content-stream emitter (already partially in <c>NetPdf.Pdf.Content</c>) consumes them.
/// </para>
/// <para>
/// <b>Disposal contract.</b> The command buffer is returned to the shared pool on
/// <see cref="Dispose"/>. <see cref="Commands"/> and accessors throw
/// <see cref="ObjectDisposedException"/> after dispose so a stale span can't read pooled
/// memory after another consumer has rented it.
/// </para>
/// <para>
/// <b>Determinism.</b> Both the command array and the side-table lists preserve insertion
/// order. Side-table indices are assigned sequentially. Two identical build sequences
/// produce two value-equal <see cref="DisplayList"/>s.
/// </para>
/// </remarks>
internal sealed class DisplayList : IDisposable
{
    private const int InitialCapacity = 64;

    // Cap at 2^28 = 268M commands ~= 16 GiB at 64 B each. A single page producing more than
    // ~10M commands almost certainly indicates a layout bug; the cap is a safety brake, not
    // a real production target.
    private const int MaxCapacity = 1 << 28;

    private DisplayCommand[]? _buffer;
    private int _count;
    private List<TextRun>? _textRuns;
    private List<RasterImage>? _images;
    private bool _disposed;

    public DisplayList()
    {
        _buffer = ArrayPool<DisplayCommand>.Shared.Rent(InitialCapacity);
    }

    /// <summary>Number of <see cref="DisplayCommand"/>s currently in the list.</summary>
    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return _count;
        }
    }

    /// <summary>
    /// View over the live commands. Valid only until the next <see cref="Add"/> (which may
    /// rent a larger backing array) or <see cref="Dispose"/>.
    /// </summary>
    public ReadOnlySpan<DisplayCommand> Commands
    {
        get
        {
            ThrowIfDisposed();
            return _buffer.AsSpan(0, _count);
        }
    }

    /// <summary>Side-table view of all text runs added so far. Empty until the first <see cref="AddTextRun"/>.</summary>
    public IReadOnlyList<TextRun> TextRuns
    {
        get
        {
            ThrowIfDisposed();
            return _textRuns ?? (IReadOnlyList<TextRun>)Array.Empty<TextRun>();
        }
    }

    /// <summary>Side-table view of all images added so far. Empty until the first <see cref="AddImage"/>.</summary>
    public IReadOnlyList<RasterImage> Images
    {
        get
        {
            ThrowIfDisposed();
            return _images ?? (IReadOnlyList<RasterImage>)Array.Empty<RasterImage>();
        }
    }

    /// <summary>
    /// Append a command. Grows the buffer (re-renting from the pool) if full.
    /// Rejects <see cref="DisplayCommandKind.None"/> — that's the uninitialized sentinel
    /// and must never reach the rendering pipeline.
    /// </summary>
    public void Add(in DisplayCommand command)
    {
        ThrowIfDisposed();
        if (command.Kind == DisplayCommandKind.None)
        {
            throw new ArgumentException(
                "Cannot append an uninitialized DisplayCommand (Kind == None). Construct " +
                "via a DisplayCommand factory (RectFill, TextRun, etc.).",
                nameof(command));
        }
        if (_count == _buffer!.Length)
        {
            Grow();
        }
        _buffer[_count++] = command;
    }

    /// <summary>
    /// Append a <see cref="TextRun"/> to the side table; returns its sequential index.
    /// Validates that <see cref="TextRun.FontSize"/> is positive and finite, and that
    /// <see cref="TextRun.GlyphIds"/> and <see cref="TextRun.Advances"/> have matching
    /// lengths when either is populated.
    /// </summary>
    public int AddTextRun(TextRun textRun)
    {
        ArgumentNullException.ThrowIfNull(textRun);
        ThrowIfDisposed();

        if (!double.IsFinite(textRun.FontSize) || textRun.FontSize <= 0)
        {
            throw new ArgumentException(
                $"TextRun.FontSize must be a positive finite number; got {textRun.FontSize}.",
                nameof(textRun));
        }
        if ((!textRun.GlyphIds.IsEmpty || !textRun.Advances.IsEmpty)
            && textRun.GlyphIds.Length != textRun.Advances.Length)
        {
            throw new ArgumentException(
                $"TextRun.GlyphIds and TextRun.Advances must have matching lengths when either is populated; " +
                $"got {textRun.GlyphIds.Length} glyph(s) vs {textRun.Advances.Length} advance(s).",
                nameof(textRun));
        }

        _textRuns ??= [];
        _textRuns.Add(textRun);
        return _textRuns.Count - 1;
    }

    /// <summary>
    /// Append a <see cref="RasterImage"/> to the side table; returns its sequential index.
    /// Validates that <see cref="RasterImage.EncodedBytes"/> is non-empty and that pixel
    /// dimensions are positive.
    /// </summary>
    public int AddImage(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ThrowIfDisposed();

        if (image.EncodedBytes.IsEmpty)
        {
            throw new ArgumentException(
                "RasterImage.EncodedBytes must not be empty.",
                nameof(image));
        }
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            throw new ArgumentException(
                $"RasterImage pixel dimensions must be positive; got {image.PixelWidth}×{image.PixelHeight}.",
                nameof(image));
        }

        _images ??= [];
        _images.Add(image);
        return _images.Count - 1;
    }

    /// <summary>Resolve a side-table text run by the index returned from <see cref="AddTextRun"/>.</summary>
    public TextRun GetTextRun(int index)
    {
        ThrowIfDisposed();
        if (_textRuns is null || (uint)index >= (uint)_textRuns.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index,
                $"Text run index out of range (count = {_textRuns?.Count ?? 0}).");
        }
        return _textRuns[index];
    }

    /// <summary>Resolve a side-table image by the index returned from <see cref="AddImage"/>.</summary>
    public RasterImage GetImage(int index)
    {
        ThrowIfDisposed();
        if (_images is null || (uint)index >= (uint)_images.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index,
                $"Image index out of range (count = {_images?.Count ?? 0}).");
        }
        return _images[index];
    }

    private void Grow()
    {
        int currentCap = _buffer!.Length;
        long newCapLong = (long)currentCap * 2;
        if (newCapLong > MaxCapacity)
        {
            throw new InvalidOperationException(
                $"DisplayList exceeded its maximum capacity of {MaxCapacity:N0} commands. " +
                "This typically indicates a runaway layout — investigate the producing painter.");
        }
        int newCap = (int)newCapLong;
        var newBuffer = ArrayPool<DisplayCommand>.Shared.Rent(newCap);
        _buffer.AsSpan(0, _count).CopyTo(newBuffer);
        // DisplayCommand has no GC references; clearArray:false avoids needless zeroing.
        ArrayPool<DisplayCommand>.Shared.Return(_buffer, clearArray: false);
        _buffer = newBuffer;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DisplayList));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_buffer is not null)
        {
            ArrayPool<DisplayCommand>.Shared.Return(_buffer, clearArray: false);
            _buffer = null;
        }
        _count = 0;
        _textRuns = null;
        _images = null;
    }
}
