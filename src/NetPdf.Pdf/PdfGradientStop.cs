// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf;

/// <summary>Phase 4 gradients — one resolved color stop for a PDF native shading:
/// the parametric <see cref="Offset"/> along the gradient axis (in [0, 1], normalized
/// per CSS Images §3.4 by the caller) and the DeviceRGB color channels (each in [0, 1]).
/// Per-stop alpha is intentionally absent — the axial/radial shadings paint opaque RGB;
/// per-stop transparency (a soft-mask alpha shading) is a documented follow-up.</summary>
public readonly record struct PdfGradientStop(double Offset, double R, double G, double B);
