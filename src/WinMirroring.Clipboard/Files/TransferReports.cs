using System.Globalization;

namespace WinMirroring.Clipboard.Files;

/// <summary>
/// Where every transfer of files into this device says how far it has got, whichever way it started — a paste, a
/// drag across the edge, a drop into a viewer window. The objects that do the pulling are made in several places,
/// some of which have no service to report to, and the indicator only showed up for the one path that did.
/// <see cref="ClipboardSyncService"/> passes all of it on as <see cref="ClipboardSyncService.TransferProgress"/>.
/// </summary>
internal static class TransferReports
{
    public static event Action<FileTransferProgress>? Reported;

    public static void Report(FileTransferProgress progress)
    {
        try
        {
            Reported?.Invoke(progress);
        }
        catch (Exception)
        {
            // An indicator must never break the transfer it shows.
        }
    }

    /// <summary>"файлов: 1234, папок: 2 (1,8 ГБ)".</summary>
    public static string Describe(FileOfferManifest manifest)
    {
        var files = manifest.Entries.Count(e => !e.IsDirectory);
        var folders = manifest.TopLevel.Count(e => e.IsDirectory);
        var parts = new List<string>();
        if (files > 0)
        {
            parts.Add($"файлов: {files}");
        }

        if (folders > 0)
        {
            parts.Add($"папок: {folders}");
        }

        return $"{string.Join(", ", parts)} ({FormatSize(manifest.TotalSize)})";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:F0} КБ"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):F1} МБ"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ"),
    };
}
