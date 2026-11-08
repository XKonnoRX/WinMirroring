namespace WinMirroring.Clipboard.Files;

/// <summary>Materialises a remote offer as real files in a cache folder (for apps that only accept CF_HDROP).</summary>
internal static class OfferDownloader
{
    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMirroring", "cache", "clipboard");

    /// <param name="onProgress">Reports total bytes copied so far and the whole size, as the copy runs.</param>
    /// <returns>Absolute paths of the top-level items, in offer order.</returns>
    public static async Task<IReadOnlyList<string>> DownloadAsync(FileOfferManifest manifest, IRemoteFileSource source, Action<long, long>? onProgress, CancellationToken ct)
    {
        var root = Path.Combine(CacheRoot, manifest.OfferId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        var total = manifest.TotalSize;
        var done = 0L;
        onProgress?.Invoke(0, total);

        var topLevel = new List<string>();
        for (var index = 0; index < manifest.Entries.Count; index++)
        {
            var entry = manifest.Entries[index];
            var target = Path.GetFullPath(Path.Combine(root, entry.Path));
            if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Refusing an offer entry outside the cache: {entry.Path}");
            }

            if (!entry.Path.Contains('\\'))
            {
                topLevel.Add(target);
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == entry.Size)
            {
                done += entry.Size; // pasted before from the same offer
                onProgress?.Invoke(done, total);
                continue;
            }

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

        onProgress?.Invoke(total, total);
        return topLevel;
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
