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
/// <param name="Refused">
/// Why nothing was read: the application that copied it marked it as content no other program may carry away, so
/// it was left alone. Null when nothing forbade reading it.
/// </param>
public sealed record ClipboardSnapshot(IReadOnlyList<ClipboardItem> Items, IReadOnlyList<string> Files, string? Refused = null)
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

/// <summary>What the receiving device is asked to do with files another device sends it.</summary>
public enum SendFilesPurpose
{
    /// <summary>Save them and open them there with their usual program.</summary>
    Open = 0,

    /// <summary>A WinMirroring build: offer to install it there (the user there decides).</summary>
    Update = 1,
}

/// <summary>
/// One device offers files to one other device, for <see cref="Purpose"/>, outside the clipboard. The receiver
/// decides whether to take them (nothing is transferred until it does) and answers with a
/// <see cref="SendFilesResult"/>.
/// </summary>
/// <param name="Note">Shown to the user there, e.g. the version of an offered build.</param>
public sealed record SendFilesRequest(uint RequestId, SendFilesPurpose Purpose, string? Note, FileOfferManifest Manifest);

/// <summary>The receiving device's answer: taken and done, or why not.</summary>
public sealed record SendFilesResult(uint RequestId, bool Ok, string? Error);
