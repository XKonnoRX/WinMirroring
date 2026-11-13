namespace WinMirroring.Clipboard.Files;

/// <summary>
/// Files copied on this device and offered to others. Only entries of a stored offer can ever be read remotely,
/// addressed by (offer id, index); a peer cannot name an arbitrary path.
/// </summary>
internal sealed class LocalOfferStore
{
    private const int MaxOffers = 8;
    private const int MaxEntries = 50_000;

    private sealed record Offer(long Id, List<FileOfferEntry> Entries, List<string?> FullPaths)
    {
        /// <summary>Kept until released: a transfer in progress must not be evicted by later clipboard copies.</summary>
        public bool Pinned { get; set; }
    }

    private const int MaxBlobs = 4;

    private readonly object _gate = new();
    private readonly LinkedList<Offer> _offers = new();
    private readonly LinkedList<(long Id, byte[] Data)> _blobs = new();

    /// <summary>Keeps an encoded clipboard message for peers to pull; returns its id.</summary>
    public long AddBlob(byte[] data)
    {
        var id = Random.Shared.NextInt64(1, long.MaxValue);
        lock (_gate)
        {
            _blobs.AddFirst((id, data));
            while (_blobs.Count > MaxBlobs)
            {
                _blobs.RemoveLast();
            }
        }

        return id;
    }

    public bool TryGetBlob(long id, out byte[] data)
    {
        lock (_gate)
        {
            foreach (var blob in _blobs)
            {
                if (blob.Id == id)
                {
                    data = blob.Data;
                    return true;
                }
            }
        }

        data = [];
        return false;
    }

    /// <param name="pinned">
    /// Keep the offer until <see cref="Release"/>: a file sent to a device on purpose must stay readable even if the
    /// user copies other files meanwhile (an evicted offer makes the transfer fail halfway).
    /// </param>
    public FileOfferManifest? Create(IReadOnlyList<string> paths, string sourceName, bool pinned = false)
    {
        var entries = new List<FileOfferEntry>();
        var fullPaths = new List<string?>();
        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var trimmed = path.TrimEnd('\\');
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name))
            {
                // A drive root such as "D:\" has no name of its own.
                name = trimmed.Replace(":", string.Empty, StringComparison.Ordinal);
            }

            name = Unique(name, topNames);
            if (Directory.Exists(trimmed))
            {
                AddDirectory(name, new DirectoryInfo(trimmed), entries, fullPaths);
            }
            else if (File.Exists(trimmed))
            {
                AddFile(name, new FileInfo(trimmed), entries, fullPaths);
            }

            if (entries.Count >= MaxEntries)
            {
                break;
            }
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var offer = new Offer(Random.Shared.NextInt64(1, long.MaxValue), entries, fullPaths) { Pinned = pinned };
        lock (_gate)
        {
            _offers.AddFirst(offer);
            Evict();
        }

        return new FileOfferManifest(offer.Id, sourceName, entries);
    }

    /// <summary>Lets a pinned offer be evicted again (its transfer finished, or failed).</summary>
    public void Release(long offerId)
    {
        lock (_gate)
        {
            foreach (var offer in _offers)
            {
                if (offer.Id == offerId)
                {
                    offer.Pinned = false;
                    break;
                }
            }

            Evict();
        }
    }

    /// <summary>Drops the oldest unpinned offers above the limit (called under the lock).</summary>
    private void Evict()
    {
        for (var node = _offers.Last; node is not null && _offers.Count > MaxOffers;)
        {
            var previous = node.Previous;
            if (!node.Value.Pinned)
            {
                _offers.Remove(node);
            }

            node = previous;
        }
    }

    public bool TryGetFile(long offerId, int index, out string fullPath)
    {
        lock (_gate)
        {
            foreach (var offer in _offers)
            {
                if (offer.Id == offerId && index >= 0 && index < offer.Entries.Count && offer.FullPaths[index] is { } path)
                {
                    fullPath = path;
                    return true;
                }
            }
        }

        fullPath = string.Empty;
        return false;
    }

    private static void AddFile(string relative, FileInfo file, List<FileOfferEntry> entries, List<string?> fullPaths)
    {
        try
        {
            entries.Add(new FileOfferEntry(relative, file.Length, false, file.LastWriteTimeUtc.Ticks));
            fullPaths.Add(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AddDirectory(string relative, DirectoryInfo directory, List<FileOfferEntry> entries, List<string?> fullPaths)
    {
        entries.Add(new FileOfferEntry(relative, 0, true, SafeTicks(directory)));
        fullPaths.Add(null);

        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (entries.Count >= MaxEntries)
            {
                return;
            }

            var childRelative = relative + "\\" + child.Name;
            switch (child)
            {
                case DirectoryInfo sub:
                    AddDirectory(childRelative, sub, entries, fullPaths);
                    break;
                case FileInfo file:
                    AddFile(childRelative, file, entries, fullPaths);
                    break;
            }
        }
    }

    private static long SafeTicks(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow.Ticks;
        }
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";
        }

        return candidate;
    }
}
