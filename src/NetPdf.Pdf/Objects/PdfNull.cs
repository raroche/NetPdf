// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>ISO 32000-2 §7.3.9 — the null object emitted as <c>null</c>.</summary>
internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    public override void WriteTo(PdfWriter writer) => writer.WriteAscii("null");
}
