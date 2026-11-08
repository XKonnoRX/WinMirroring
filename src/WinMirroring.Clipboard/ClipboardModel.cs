namespace WinMirroring.Clipboard;

/// <summary>Clipboard formats that travel between devices. Values are the wire ids.</summary>
public enum ClipboardItemKind : byte
{
    UnicodeText = 1,
    Html = 2,
    Rtf = 3,
    Png = 4,
    Dib = 5,
}

/// <summary>One clipboard format with its raw bytes exactly as Windows stores them.</summary>
public sealed record ClipboardItem(ClipboardItemKind Kind, byte[] Data);

/// <summary>What was read from the local clipboard: either data formats or a list of file paths.</summary>
public sealed record ClipboardSnapshot(IReadOnlyList<ClipboardItem> Items, IReadOnlyList<string> Files)
{
    public bool IsEmpty => Items.Count == 0 && Files.Count == 0;

    public long DataSize => Items.Sum(i => (long)i.Data.Length);
}

/// <summary>One file or folder of a file offer; <see cref="Path"/> is relative, with backslashes, top level first.</summary>
public sealed record FileOfferEntry(string Path, long Size, bool IsDirectory, long LastWriteUtcTicks);

/// <summary>Files being dragged across devices; <see cref="SourceId"/> is the device that serves their contents.</summary>
public sealed record DragPayload(string SourceId, FileOfferManifest Manifest);

/// <summary>
/// Drag and drop through a screen viewer window. <see cref="X"/>/<see cref="Y"/> are normalized coordinates in the
/// stream <see cref="StreamId"/>; <see cref="Payload"/> carries files for a drop, or what a catch found.
/// </summary>
public sealed record ViewerDragMessage(uint StreamId, float X, float Y, uint RequestId, DragPayload? Payload);

/// <summary>What a drag hovering over our drop surface carried: real local paths, or a drag we started ourselves.</summary>
public sealed record DragCatch(IReadOnlyList<string>? LocalPaths, DragPayload? Forwarded);

/// <summary>Files copied on another device. Contents stay there until a paste asks for them.</summary>
public sealed record FileOfferManifest(long OfferId, string SourceName, List<FileOfferEntry> Entries)
{
    public long TotalSize => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

    /// <summary>Entries without a backslash: what the user actually selected.</summary>
    public IEnumerable<FileOfferEntry> TopLevel => Entries.Where(e => !e.Path.Contains('\\'));
}

/// <summary>Progress of materialising a received file offer locally, for a progress indicator.</summary>
public sealed record FileTransferProgress(long OfferId, string What, long Done, long Total, bool Finished, bool Ok);
