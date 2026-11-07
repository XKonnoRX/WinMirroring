namespace WinMirroring.Clipboard.Files;

/// <summary>Materialises a remote offer as real files in a cache folder (for apps that only accept CF_HDROP).</summary>
internal static class OfferDownloader
{
    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMirroring", "cache", "clipboard");

    /// <returns>Absolute paths of the top-level items, in offer order.</returns>
    public static async Task<IReadOnlyList<string>> DownloadAsync(FileOfferManifest manifest, IRemoteFileSource source, CancellationToken ct)
    {
        var root = Path.Combine(CacheRoot, manifest.OfferId.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

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
                continue; // pasted before from the same offer
            }

            var partial = target + ".part";
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await using (var input = new RemoteFileStream(source, manifest.OfferId, index, entry.Size))
            {
                await input.CopyToAsync(output, RemoteFileStream.ChunkSize, ct).ConfigureAwait(false);
            }

            File.Move(partial, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc));
        }

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
