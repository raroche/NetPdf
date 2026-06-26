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
///   <item>No <see cref="PdfStream"/> appears as a child of another container (dictionary
///     or array) anywhere in the graph — per ISO 32000-2 §7.3.8 streams shall be indirect
///     objects, so any nested direct stream would emit malformed PDF bytes. The only
///     legitimate position for a stream is at the top of an indirect-object slot.</item>
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
        // Per Phase D D-6 — walk every dictionary in the document graph
        // looking for active-content keys (/OpenAction, /AA, /JavaScript,
        // /Launch, /URI, /SubmitForm, /ImportData, /GoToR, /GoToE,
        // /EmbeddedFile, /Names/EmbeddedFiles). Phase 1's PDF writer never
        // emits any of these (verified by the Phase B B-6 contract test
        // over SmokeDocumentFactory output) but as Phase 4 wires
        // annotations + Phase 5 wires links, an accidental introduction
        // is a real risk. The preflight catches it BEFORE bytes are
        // written, in a unit-testable way (negative tests use
        // constructed PdfDictionary objects).
        ValidateNoActiveContent(writer.Objects, writer.Trailer, writer.AllowUriLinkAnnotations);
    }

    /// <summary>Per Phase D D-6 — walk dictionaries + reject any
    /// active-content key. Closed denylist; covers the surfaces that
    /// the major HTML-to-PDF CVE survey calls out (Apryse argument-
    /// injection RCE via <c>/Launch</c>, generic JS-action surfaces,
    /// embedded-file egress).</summary>
    /// <remarks>
    /// <para>The denylist is keyed on PDF dictionary names. Each
    /// dictionary is checked for explicit prohibited keys; the
    /// values themselves are NOT walked further (a benign dictionary
    /// that happens to contain <c>"OpenAction"</c> as a string value
    /// inside, e.g., a content-stream comment, is irrelevant — only
    /// the dictionary KEYS matter for action dispatching).</para>
    /// <para>Per PR #18 Copilot review #10 — the visited set is
    /// shared across the whole walk (was per-store-entry + per-trailer,
    /// causing repeated allocation + redundant traversal of objects
    /// reachable from multiple roots). Per Copilot review #9 — the
    /// active-content keys are precomputed as a static
    /// <see cref="PdfName"/> array (was allocating a new
    /// <see cref="PdfName"/> per key per dictionary visited).</para>
    /// </remarks>
    private static void ValidateNoActiveContent(IndirectObjectStore store, PdfDictionary trailer, bool allowUriLinkActions)
    {
        var visited = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < store.Count; i++)
        {
            VisitDictionariesForActiveContent(store.AllEntries[i].Object!, visited, allowUriLinkActions);
        }
        VisitDictionariesForActiveContent(trailer, visited, allowUriLinkActions);
    }

    /// <summary>Per Phase D D-6 — names that, as dictionary keys,
    /// indicate an active-content action or embedded-file surface
    /// NetPdf v1 must never emit.</summary>
    private static readonly string[] ActiveContentKeyNames =
    [
        "OpenAction",   // catalog action: runs on document open
        "AA",           // additional actions: focus / blur / open / etc.
        "JavaScript",   // JS object / action body
        "JS",           // JS script body inside an action
        "Launch",       // launches an external program
        "URI",          // /URI action — external link
        "SubmitForm",   // posts form data to a URL
        "ImportData",   // imports form data from a URL
        "GoToR",        // remote GoTo — fetches a URL
        "GoToE",        // GoTo embedded — references an embedded file
        "EmbeddedFile", // embedded file substream
        "EmbeddedFiles",// /Names entry exposing embedded files
        "RichMedia",    // RichMedia annotation (Flash / 3D)
    ];

    /// <summary>Per PR #18 Copilot review #9 — precomputed
    /// <see cref="PdfName"/> instances for each entry in
    /// <see cref="ActiveContentKeyNames"/>. Pre-fix every dictionary
    /// visited allocated 13 fresh <see cref="PdfName"/>s; on a large
    /// document with hundreds of dictionaries that totaled thousands
    /// of throwaway allocations during preflight.</summary>
    private static readonly PdfName[] ActiveContentKeys = BuildActiveContentKeys();

    private static PdfName[] BuildActiveContentKeys()
    {
        var arr = new PdfName[ActiveContentKeyNames.Length];
        for (var i = 0; i < arr.Length; i++) arr[i] = new PdfName(ActiveContentKeyNames[i]);
        return arr;
    }

    private static void VisitDictionariesForActiveContent(PdfObject obj, HashSet<PdfObject> visited, bool allowUriLinkActions)
    {
        if (obj is PdfIndirectRef) return; // refs are walked at the store level
        if (!visited.Add(obj)) return;
        if (obj is PdfDictionary dict)
        {
            for (var i = 0; i < ActiveContentKeys.Length; i++)
            {
                if (dict.Get(ActiveContentKeys[i]) is null) continue;
                // The single opt-in (Phase 4 links): a /URI key is allowed INSIDE a well-formed URI action
                // (/S /URI) when AllowUriLinkAnnotations is set. Every other key — and a /URI outside a URI
                // action — still throws. This unblocks external hyperlinks without opening the JS / Launch /
                // SubmitForm / embedded-file surfaces.
                if (allowUriLinkActions
                    && ActiveContentKeyNames[i] == "URI"
                    && dict.Get(PdfNames.S) is PdfName s && s.Value == "URI")
                {
                    continue;
                }
                throw new InvalidOperationException(
                    $"PdfPreflightValidator: dictionary contains the active-content key '/{ActiveContentKeyNames[i]}', "
                    + "which NetPdf v1 must never emit. If this is intentional, the writer needs "
                    + "an explicit allowlist gate (currently no such opt-in exists).");
            }
        }
        foreach (var child in obj.EnumerateChildren())
        {
            VisitDictionariesForActiveContent(child, visited, allowUriLinkActions);
        }
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

        // /Root must resolve to the document Catalog: a dictionary with /Type /Catalog.
        // Any other shape (non-dict target, dict without /Type, dict with /Type other than
        // /Catalog) would produce an invalid PDF, so reject up front.
        var target = store.Get(rootRef);
        if (target is not PdfDictionary rootDict)
        {
            throw new InvalidOperationException(
                $"Trailer /Root must reference a dictionary; got {target?.GetType().Name ?? "null"}.");
        }
        var typeValue = rootDict.Get(PdfNames.Type);
        if (typeValue is null)
        {
            throw new InvalidOperationException(
                "Trailer /Root references a dictionary missing the /Type entry. " +
                "Expected /Type /Catalog.");
        }
        if (typeValue is not PdfName typeName)
        {
            throw new InvalidOperationException(
                $"Trailer /Root dictionary has /Type of {typeValue.GetType().Name}; expected a PdfName /Catalog.");
        }
        if (!typeName.Equals(PdfNames.Catalog))
        {
            throw new InvalidOperationException(
                $"Trailer /Root dictionary has /Type /{typeName.Value}; expected /Catalog.");
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

        // Each store entry is the content of one indirect object slot. A PdfStream is only
        // valid at this top level, so we pass isTopOfIndirectObject=true here and the
        // recursive call flips it to false for every descendant.
        for (int i = 0; i < store.Count; i++)
        {
            ValidateRecursive(store.AllEntries[i].Object!, store, currentPath, isTopOfIndirectObject: true);
        }

        // Walk the trailer too — every entry (/Root, /Info, /Encrypt, future additions) is
        // part of the structural graph and its refs / cycles must be checked. The trailer
        // dictionary itself is not an indirect-object slot, so its descendants cannot be
        // direct streams (isTopOfIndirectObject=false from the start).
        ValidateRecursive(trailer, store, currentPath, isTopOfIndirectObject: false);
    }

    private static void ValidateRecursive(PdfObject obj, IndirectObjectStore store, HashSet<PdfObject> currentPath, bool isTopOfIndirectObject)
    {
        // Indirect references don't create cycles — they're pointers, not inline structure.
        // Validate the ref itself and stop (the target object is validated when the store
        // walk reaches it).
        if (obj is PdfIndirectRef reference)
        {
            ValidateReference(reference, store);
            return;
        }

        // ISO 32000-2 §7.3.8: "A stream object … shall be an indirect object". A
        // PdfStream that surfaces as a child of another container would emit
        // `<<...>>\nstream\n...endstream` inline — invalid PDF. The image-registration
        // path now allocates indirect slots for SMasks; this preflight check catches any
        // lingering or third-party caller that bypasses it.
        if (obj is PdfStream && !isTopOfIndirectObject)
        {
            throw new InvalidOperationException(
                "Direct PdfStream found nested inside another container (dict or array). " +
                "Streams must be indirect objects per ISO 32000-2 §7.3.8 — wrap the stream " +
                "with IndirectObjectStore.Add() and reference it via the resulting " +
                "PdfIndirectRef. (For image XObjects with SMasks, register them through " +
                "PdfDocument.RegisterImage(ImageXObjectResult) which performs this wiring " +
                "automatically.)");
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
                ValidateRecursive(child, store, currentPath, isTopOfIndirectObject: false);
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
