// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;

namespace NetPdf.Pdf;

/// <summary>
/// Runs structural checks on a <see cref="PdfDocumentWriter"/> before any bytes are written,
/// turning convention-based invariants into hard failures. Every check produces a clear
/// <see cref="InvalidOperationException"/> identifying the violation; consumers see bugs at
/// the API boundary instead of corrupt PDFs at the consumer's end.
/// <para>Checks performed:</para>
/// <list type="bullet">
///   <item>PDF version is on the supported allow-list.</item>
///   <item>All allocated indirect-object slots have an assigned object.</item>
///   <item>Trailer <c>/Root</c> is present, an indirect ref, allocated, and (when its target
///     is a dictionary) has <c>/Type /Catalog</c>.</item>
///   <item>If trailer <c>/ID</c> is set explicitly, its shape conforms to §14.4
///     (array of exactly two byte strings).</item>
///   <item>Every reachable indirect ref — in any store-owned object OR anywhere in the
///     trailer — points to an allocated object, has generation 0, and either has
///     <c>StoreId == 0</c> (synthetic, opaque pointer) or matches the document's store id
///     (no foreign-store leakage).</item>
///   <item>The graph walk is cycle-safe (visited set, reference identity).</item>
/// </list>
/// </summary>
internal static class PdfPreflightValidator
{
    public static void Validate(PdfDocumentWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        ValidateVersion(writer.Version);
        writer.Objects.ValidateAllAssigned();
        ValidateTrailerRootShape(writer.Trailer, writer.Objects);
        ValidateExplicitIdShape(writer.Trailer);
        ValidateGraph(writer.Objects, writer.Trailer);
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrEmpty(version) || !PdfFormat.SupportedVersionSet.Contains(version))
        {
            throw new InvalidOperationException(
                $"PDF version '{version}' is not supported. " +
                $"Supported: {string.Join(", ", PdfFormat.SupportedVersions)}.");
        }
    }

    private static void ValidateTrailerRootShape(PdfDictionary trailer, IndirectObjectStore store)
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
        if (store.Get(rootRef) is PdfDictionary rootDict
            && rootDict.Get(PdfNames.Type) is PdfName typeName
            && !typeName.Equals(PdfNames.Catalog))
        {
            throw new InvalidOperationException(
                $"Trailer /Root points to a dictionary whose /Type is /{typeName.Value}; expected /Catalog.");
        }
    }

    /// <summary>
    /// Per ISO 32000-2 §14.4: <c>/ID</c> shall be an array of two byte strings. If the user
    /// supplied a value, validate its shape eagerly so malformed metadata never reaches
    /// the byte stream.
    /// </summary>
    private static void ValidateExplicitIdShape(PdfDictionary trailer)
    {
        var idValue = trailer.Get(PdfNames.ID);
        if (idValue is null) return;

        if (idValue is not PdfArray idArray)
        {
            throw new InvalidOperationException(
                $"Trailer /ID must be a PdfArray (got {idValue.GetType().Name}).");
        }
        if (idArray.Count != 2)
        {
            throw new InvalidOperationException(
                $"Trailer /ID must contain exactly two byte strings (got {idArray.Count} elements).");
        }
        for (int i = 0; i < 2; i++)
        {
            var entry = idArray[i];
            if (entry is not (PdfHexString or PdfLiteralString))
            {
                throw new InvalidOperationException(
                    $"Trailer /ID element {i} must be a byte string (PdfHexString or PdfLiteralString); " +
                    $"got {entry.GetType().Name}.");
            }
        }
    }

    private static void ValidateGraph(IndirectObjectStore store, PdfDictionary trailer)
    {
        // currentPath tracks ancestors in the active recursion (reference identity). An object
        // appearing in its own ancestry is a direct cycle in the inline object graph and is
        // rejected — emission would otherwise stack-overflow because direct objects are
        // emitted inline (PdfIndirectRef breaks the cycle by emitting a pointer instead).
        // Sharing a non-cyclic direct subtree from two siblings is permitted; emission
        // duplicates the bytes, which is wasteful but well-formed.
        var currentPath = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);

        for (int i = 0; i < store.Count; i++)
        {
            ValidateRecursive(store.AllEntries[i].Object!, store, currentPath);
        }

        // Walk the trailer too — every entry (/Root, /Info, /Encrypt, future additions) is
        // part of the structural graph and its refs / cycles must be checked.
        ValidateRecursive(trailer, store, currentPath);
    }

    private static void ValidateRecursive(PdfObject obj, IndirectObjectStore store, HashSet<PdfObject> currentPath)
    {
        // Indirect references don't create cycles — they're pointers, not inline structure.
        // Validate the ref itself and stop (the target object is validated when the store
        // walk reaches it).
        if (obj is PdfIndirectRef reference)
        {
            ValidateReference(reference, store);
            return;
        }

        if (!currentPath.Add(obj))
        {
            throw new InvalidOperationException(
                "Direct cycle in object graph: a non-indirect PDF object appears as its own " +
                "descendant. Wrap it in an indirect reference (IndirectObjectStore.Add) if you " +
                "need to express a back-pointer or shared cyclic structure.");
        }

        try
        {
            foreach (var child in obj.EnumerateChildren())
            {
                ValidateRecursive(child, store, currentPath);
            }
        }
        finally
        {
            currentPath.Remove(obj);
        }
    }

    private static void ValidateReference(PdfIndirectRef reference, IndirectObjectStore store)
    {
        if (reference.Generation != 0)
        {
            throw new InvalidOperationException(
                $"Indirect reference to object {reference.ObjectNumber} has generation " +
                $"{reference.Generation}; v1 only supports generation 0.");
        }
        if (reference.StoreId != 0 && reference.StoreId != store.Id)
        {
            throw new InvalidOperationException(
                $"Indirect reference to object {reference.ObjectNumber} carries StoreId " +
                $"{reference.StoreId}, but this document's store is {store.Id}; " +
                "cross-store references would silently retarget if accepted.");
        }
        if (!store.HasAllocatedNumber(reference))
        {
            throw new InvalidOperationException(
                $"Indirect reference to object {reference.ObjectNumber} is dangling " +
                $"(only {store.Count} objects allocated in the store).");
        }
    }
}
