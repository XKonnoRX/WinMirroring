using WinMirroring.Clipboard.Protocol;

namespace WinMirroring.Clipboard.Files;

/// <summary>Materialises a remote offer as real files in a folder (the cache, or wherever the caller wants them).</summary>
internal static class OfferDownloader
{
    /// <summary>Files up to this size are written on worker threads, several at once, while the next ones arrive.</summary>
    private const int SmallFile = 1024 * 1024;
    private const int ParallelWrites = 6;

    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMirroring", "cache", "clipboard");

    /// <param name="onProgress">Reports total bytes copied so far and the whole size, as the copy runs.</param>
    /// <param name="targetRoot">Where to put the files; by default a folder of this offer inside the cache.</param>
    /// <returns>Absolute paths of the top-level items, in offer order.</returns>
    public static async Task<IReadOnlyList<string>> DownloadAsync(
        FileOfferManifest manifest,
        IRemoteFileSource source,
        Action<long, long>? onProgress,
        CancellationToken ct,
        string? targetRoot = null)
    {
        var root = targetRoot ?? Path.Combine(CacheRoot, manifest.OfferId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        var total = manifest.TotalSize;
        var done = 0L;
        onProgress?.Invoke(0, total);

        // Where every entry goes, checked once; folders made up front, files already there (pasted before from the
        // same offer) left alone.
        var targets = new string[manifest.Entries.Count];
        var needed = new bool[manifest.Entries.Count];
        var topLevel = new List<string>();
        for (var index = 0; index < manifest.Entries.Count; index++)
        {
            var entry = manifest.Entries[index];
            var target = Path.GetFullPath(Path.Combine(root, entry.Path));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Refusing an offer entry outside the target folder: {entry.Path}");
            }

            targets[index] = target;
            if (!entry.Path.Contains('\\'))
            {
                topLevel.Add(target);
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == entry.Size)
            {
                done += entry.Size;
                continue;
            }

            needed[index] = true;
        }

        onProgress?.Invoke(done, total);
        var first = Array.IndexOf(needed, true);
        if (first < 0)
        {
            onProgress?.Invoke(total, total);
            return topLevel;
        }

        var bundle = await source.OpenBundleAsync(manifest.OfferId, first, manifest.Entries.Count, ct).ConfigureAwait(false);
        if (bundle is not null)
        {
            await using (bundle.ConfigureAwait(false))
            {
                await ReceiveBundleAsync(manifest, bundle, targets, needed, done, total, onProgress, ct).ConfigureAwait(false);
            }
        }
        else
        {
            await ReceiveChunkByChunkAsync(manifest, source, targets, needed, done, total, onProgress, ct).ConfigureAwait(false);
        }

        onProgress?.Invoke(total, total);
        return topLevel;
    }

    /// <summary>
    /// The whole offer as one stream: a small file is gathered in memory and handed to a writer thread at its end,
    /// so creating thousands of files here overlaps with receiving them; a large one is written as it comes.
    /// </summary>
    private static async Task ReceiveBundleAsync(
        FileOfferManifest manifest,
        IBundleReader bundle,
        string[] targets,
        bool[] needed,
        long done,
        long total,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        using var writers = new SemaphoreSlim(ParallelWrites);
        var writes = new List<Task>();
        var lastReport = Environment.TickCount64;

        int current = -1;
        long arrived = 0;
        MemoryStream? small = null;
        FileStream? large = null;

        try
        {
            while (await bundle.ReadAsync(ct).ConfigureAwait(false) is { } record)
            {
                var index = record.Index;
                if (index < 0 || index >= targets.Length || manifest.Entries[index].IsDirectory)
                {
                    throw new IOException("The other device sent a file that is not in the offer.");
                }

                if (index != current)
                {
                    current = index;
                    arrived = 0;
                    small?.Dispose();
                    small = null;
                    if (large is not null)
                    {
                        await large.DisposeAsync().ConfigureAwait(false);
                        large = null;
                    }
                }

                var entry = manifest.Entries[index];
                if (record.Kind == BundleRecordKind.Data)
                {
                    // Everything here goes by the size the offer announced: the progress, the memory a small file is
                    // gathered in, the room on the disk. A device that sends more is stopped at the first byte too
                    // many — not at the file's end record, which it need never send while this fills up.
                    arrived += record.Data.Length;
                    if (arrived > entry.Size)
                    {
                        if (large is not null)
                        {
                            await large.DisposeAsync().ConfigureAwait(false);
                            large = null;
                            File.Delete(targets[index] + ".part");
                        }

                        throw new IOException($"The other device sent more of «{entry.Path}» than it announced.");
                    }
                }

                switch (record.Kind)
                {
                    case BundleRecordKind.Data when needed[index]:
                        if (entry.Size <= SmallFile)
                        {
                            small ??= new MemoryStream((int)entry.Size);
                            small.Write(record.Data.Span);
                        }
                        else
                        {
                            large ??= Create(targets[index] + ".part");
                            await large.WriteAsync(record.Data, ct).ConfigureAwait(false);
                        }

                        done += record.Data.Length;
                        break;

                    case BundleRecordKind.Data:
                        done += record.Data.Length; // there already: arrives because it is in the range, and is skipped
                        break;

                    case BundleRecordKind.End when needed[index]:
                        var target = targets[index];
                        var stamp = new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc);
                        if (large is not null)
                        {
                            var length = large.Length;
                            await large.DisposeAsync().ConfigureAwait(false);
                            large = null;
                            Finish(target, length, entry, stamp);
                        }
                        else
                        {
                            var bytes = small?.ToArray() ?? [];
                            small?.Dispose();
                            small = null;
                            await writers.WaitAsync(ct).ConfigureAwait(false);
                            writes.Add(Task.Run(() =>
                            {
                                try
                                {
                                    WriteSmall(target, bytes, entry, stamp);
                                }
                                finally
                                {
                                    writers.Release();
                                }
                            }, CancellationToken.None));
                        }

                        current = -1;
                        break;

                    case BundleRecordKind.Error:
                        throw new IOException(record.Status == FileChunkStatus.NotFound
                            ? "The other device no longer offers these files (its clipboard changed)."
                            : $"The other device could not read «{entry.Path}».");
                }

                // A progress line per record would be thousands of them a second for small files.
                if (Environment.TickCount64 - lastReport >= 100)
                {
                    lastReport = Environment.TickCount64;
                    onProgress?.Invoke(done, total);
                }

                // A write that failed stops the transfer now rather than at the end.
                if (writes.Count > 256)
                {
                    await Task.WhenAll(writes).ConfigureAwait(false);
                    writes.Clear();
                }
            }

            await Task.WhenAll(writes).ConfigureAwait(false);
        }
        finally
        {
            small?.Dispose();
            if (large is not null)
            {
                await large.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static FileStream Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
    }

    private static void WriteSmall(string target, byte[] bytes, FileOfferEntry entry, DateTime stamp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target + ".part", bytes);
        Finish(target, bytes.Length, entry, stamp);
    }

    private static void Finish(string target, long length, FileOfferEntry entry, DateTime stamp)
    {
        if (length != entry.Size)
        {
            File.Delete(target + ".part");
            throw new IOException($"«{entry.Path}» changed on the other device while it was being sent.");
        }

        File.Move(target + ".part", target, overwrite: true);
        File.SetLastWriteTimeUtc(target, stamp);
    }

    /// <summary>The way it was done before bundles, for a device that does not send them.</summary>
    private static async Task ReceiveChunkByChunkAsync(
        FileOfferManifest manifest,
        IRemoteFileSource source,
        string[] targets,
        bool[] needed,
        long done,
        long total,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        for (var index = 0; index < manifest.Entries.Count; index++)
        {
            if (!needed[index])
            {
                continue;
            }

            var entry = manifest.Entries[index];
            var target = targets[index];
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var partial = target + ".part";
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await using (var input = new RemoteFileStream(source, manifest.OfferId, index, entry.Size))
            {
                var buffer = new byte[RemoteFileStream.ChunkSize];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    onProgress?.Invoke(done, total);
                }
            }

            File.Move(partial, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
        }
    }

    /// <summary>Deletes cached offers older than a day.</summary>
    public static void CleanUp()
    {
        try
        {
            var root = new DirectoryInfo(CacheRoot);
            if (!root.Exists)
            {
                return;
            }

            foreach (var offer in root.EnumerateDirectories())
            {
                if (DateTime.UtcNow - offer.CreationTimeUtc > TimeSpan.FromDays(1))
                {
                    offer.Delete(recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
