// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using NetPdf.Pdf.Images;

namespace NetPdf.Rendering;

/// <summary>Phase 4 filters (PR 2) — converts a parsed <see cref="CssFilter"/> into the PDF-layer
/// <see cref="ImageFilterStep"/> list the Skia <see cref="ImageFilterApplier"/> consumes, and builds
/// a stable dedup key. The supported-kind set grows per task — this cut handles the proportional
/// color-matrix functions; <c>blur</c> / <c>drop-shadow</c> are added later.</summary>
internal static class ImageFilterBridge
{
    /// <summary>Map <paramref name="filter"/> to applier steps (resolving a <c>drop-shadow</c> color
    /// against <paramref name="currentColorArgb"/>). Returns <see langword="false"/> when any function
    /// isn't supported in this cut (the caller then shows the image UNFILTERED — never silently
    /// wrong).</summary>
    public static bool TryBuildSteps(CssFilter filter, uint currentColorArgb, out List<ImageFilterStep> steps)
    {
        steps = new List<ImageFilterStep>(filter.Ops.Count);
        foreach (var op in filter.Ops)
        {
            if (op.Kind == FilterKind.DropShadow)
            {
                var sh = op.Shadow!.Value;
                uint argb;
                if (sh.ColorRaw is null) argb = currentColorArgb;
                else
                {
                    var resolved = ColorResolver.Resolve(sh.ColorRaw, PropertyId.Color, "color", diagnostics: null, location: default);
                    if (!FragmentPainter.TryResolveColor(resolved.Slot, currentColorArgb, out argb)) { steps.Clear(); return false; }
                }
                FragmentPainter.ColorChannels(argb, out var r, out var g, out var b);
                steps.Add(new ImageFilterStep(
                    ImageFilterKind.DropShadow, 0, sh.OffsetXPx, sh.OffsetYPx, sh.BlurPx,
                    r, g, b, FragmentPainter.Alpha(argb) / 255.0));
                continue;
            }
            ImageFilterKind? kind = op.Kind switch
            {
                FilterKind.Grayscale => ImageFilterKind.Grayscale,
                FilterKind.Sepia => ImageFilterKind.Sepia,
                FilterKind.Invert => ImageFilterKind.Invert,
                FilterKind.Brightness => ImageFilterKind.Brightness,
                FilterKind.Contrast => ImageFilterKind.Contrast,
                FilterKind.Saturate => ImageFilterKind.Saturate,
                FilterKind.HueRotate => ImageFilterKind.HueRotate,
                FilterKind.Opacity => ImageFilterKind.Opacity,
                FilterKind.Blur => ImageFilterKind.Blur,
                _ => null,
            };
            if (kind is null) { steps.Clear(); return false; }
            steps.Add(new ImageFilterStep(kind.Value, op.Amount));
        }
        return steps.Count > 0;
    }

    /// <summary>A canonical dedup key for a filter (its ordered kinds + amounts), so two
    /// <c>&lt;img&gt;</c>s with the same source + filter share one filtered XObject.</summary>
    public static string FilterKey(CssFilter filter)
    {
        var sb = new StringBuilder(filter.Ops.Count * 16);
        foreach (var op in filter.Ops)
        {
            sb.Append((int)op.Kind).Append(':')
              .Append(op.Amount.ToString("R", CultureInfo.InvariantCulture));
            if (op.Shadow is { } s)
                sb.Append('|').Append(s.OffsetXPx.ToString("R", CultureInfo.InvariantCulture))
                  .Append(',').Append(s.OffsetYPx.ToString("R", CultureInfo.InvariantCulture))
                  .Append(',').Append(s.BlurPx.ToString("R", CultureInfo.InvariantCulture))
                  .Append(',').Append(s.ColorRaw);
            sb.Append(';');
        }
        return sb.ToString();
    }
}
