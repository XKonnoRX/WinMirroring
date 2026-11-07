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

/// <summary>Files copied on another device. Contents stay there until a paste asks for them.</summary>
public sealed record FileOfferManifest(long OfferId, string SourceName, List<FileOfferEntry> Entries)
{
    public long TotalSize => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

    /// <summary>Entries without a backslash: what the user actually selected.</summary>
    public IEnumerable<FileOfferEntry> TopLevel => Entries.Where(e => !e.Path.Contains('\\'));
}
