// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Svg;

/// <summary>
/// SEC-8 — why an SVG failed to parse, so the caller can emit a specific diagnostic instead of a silent
/// null: <see cref="Blocked"/> = rejected by a security guard (a prohibited DTD/entity or an over-size
/// document — the XXE / entity-expansion "billion laughs" defense); <see cref="Malformed"/> = not
/// well-formed XML; <see cref="NotSvg"/> = parsed but the root isn't <c>&lt;svg&gt;</c>;
/// <see cref="Ok"/> = parsed successfully.
/// </summary>
internal enum SvgParseStatus
{
    Ok,
    NotSvg,
    Malformed,
    Blocked,
}
