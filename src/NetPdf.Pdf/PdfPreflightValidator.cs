// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// Runs structural checks on a <see cref="PdfDocumentWriter"/> before any bytes are written,
/// turning convention-based invariants into hard failures. Every check produces a clear
/// <see cref="InvalidOperationException"/> identifying the violation; consumers see bugs at
/// the API boundary instead of corrupt PDFs at the consumer's end.
/// </summary>
internal static class PdfPreflightValidator
{
    public static void Validate(PdfDocumentWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        ValidateVersion(writer.Version);
        writer.Objects.ValidateAllAssigned();
        ValidateTrailer(writer.Trailer, writer.Objects);
        ValidateGraph(writer.Objects);
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrEmpty(version) || !PdfFormat.SupportedVersions.Contains(version))
        {
            throw new InvalidOperationException(
                $"PDF version '{version}' is not supported. " +
                $"Supported: {string.Join(", ", PdfFormat.SupportedVersions)}.");
        }
    }

    private static void ValidateTrailer(PdfDictionary trailer, IndirectObjectStore store)
    {
        var rootValue = trailer.Get(PdfNames.Root);
        if (rootValue is null)
        {
            throw new InvalidOperationException(
                "Trailer is missing required /Root entry. Set Trailer.Set(PdfNames.Root, catalogRef).");
        }
        if (rootValue is not PdfIndirectRef rootRef)
        {
            throw new InvalidOperationException(
                $"Trailer /Root must be an indirect reference (got {rootValue.GetType().Name}).");
        }
        if (!store.HasAllocatedNumber(rootRef))
        {
            throw new InvalidOperationException(
                $"Trailer /Root points to object {rootRef.ObjectNumber}, " +
                $"which is not allocated in the store (only {store.Count} objects exist).");
        }

        // Light type check: if /Root points to a dictionary, its /Type should be /Catalog.
        // Catches the common bug of pointing /Root at the page tree by mistake.
        if (store.Get(rootRef) is PdfDictionary rootDict)
        {
            if (rootDict.Get(PdfNames.Type) is PdfName typeName && !typeName.Equals(PdfNames.Catalog))
            {
                throw new InvalidOperationException(
                    $"Trailer /Root points to a dictionary whose /Type is /{typeName.Value}; expected /Catalog.");
            }
        }
    }

    private static void ValidateGraph(IndirectObjectStore store)
    {
        for (int i = 0; i < store.Count; i++)
        {
            var obj = store.AllEntries[i].Object!;
            ValidateRecursive(obj, store);
        }
    }

    private static void ValidateRecursive(PdfObject obj, IndirectObjectStore store)
    {
        if (obj is PdfIndirectRef reference)
        {
            if (reference.Generation != 0)
            {
                throw new InvalidOperationException(
                    $"Indirect reference to object {reference.ObjectNumber} has generation " +
                    $"{reference.Generation}; v1 only supports generation 0.");
            }
            if (reference.Generation > PdfFormat.MaxGeneration)
            {
                throw new InvalidOperationException(
                    $"Indirect reference generation {reference.Generation} exceeds the " +
                    $"5-digit xref field limit ({PdfFormat.MaxGeneration}).");
            }
            if (!store.HasAllocatedNumber(reference))
            {
                throw new InvalidOperationException(
                    $"Indirect reference to object {reference.ObjectNumber} is dangling " +
                    $"(only {store.Count} objects allocated in the store).");
            }
            return;
        }

        foreach (var child in obj.EnumerateChildren())
        {
            ValidateRecursive(child, store);
        }
    }
}
