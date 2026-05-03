// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>ISO 32000-2 §7.3.2 — boolean object emitted as <c>true</c> or <c>false</c>.</summary>
internal sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    public bool Value { get; }

    private PdfBoolean(bool value) => Value = value;

    public static PdfBoolean From(bool value) => value ? True : False;

    public override void WriteTo(PdfWriter writer)
    {
        writer.WriteAscii(Value ? "true" : "false");
    }
}
