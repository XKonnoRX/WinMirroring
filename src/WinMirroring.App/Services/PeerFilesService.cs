using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WinMirroring.Clipboard;
using WinMirroring.Core;
using WinMirroring.Core.Peers;

namespace WinMirroring.App.Services;

/// <summary>
/// Sending files straight to another device: "open this there", and handing this device's own build to a device
/// running an older one (which is how a device with no access to GitHub gets updated). The transfer itself is the
/// clipboard service's; this decides what the files mean and, on the receiving side, asks the user before anything
/// is opened or installed — a paired device must not be able to run something here on its own.
/// </summary>
public sealed class PeerFilesService
{
    private readonly MirroringNode _node;
    private readonly ClipboardSyncService _clipboard;
    private readonly DeviceInfoService _devices;
    private readonly GithubUpdater _updater;
    private readonly ILogger<PeerFilesService> _logger;

    public PeerFilesService(
        MirroringNode node,
        ClipboardSyncService clipboard,
        DeviceInfoService devices,
        GithubUpdater updater,
        ILogger<PeerFilesService> logger)
    {
        _node = node;
        _clipboard = clipboard;
        _devices = devices;
        _updater = updater;
        _logger = logger;
    }

    /// <summary>Asks the user here a yes-or-no question (set by the app; without it nothing is taken).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Shows a notice here (title, text).</summary>
    public Action<string, string>? Notify { get; set; }

    /// <summary>Where files taken from another device are put.</summary>
    public static string ReceivedDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMirroring", "received");

    public void Start() => _clipboard.FilesOffered += OnFilesOffered;

    // ---- sending -----------------------------------------------------------------------------------------

    /// <summary>Sends <paramref name="paths"/> to a device and asks it to open them. Returns what to show the user.</summary>
    public async Task<string> OpenOnDeviceAsync(string peerId, string peerName, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return "Нечего отправлять.";
        }

        var what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Count} файл(ов)";
        _logger.LogInformation("Sending {What} to {Peer} to open", what, peerName);
        var result = await _clipboard.SendFilesAsync(peerId, paths, SendFilesPurpose.Open, what);
        return result.Ok
            ? $"«{what}» открыто на {peerName}."
            : $"Не получилось открыть «{what}» на {peerName}: {result.Error}";
    }

    /// <summary>
    /// Hands this device's own build to <paramref name="peerId"/> so it can install it. Nothing is downloaded from
    /// the internet: the build is zipped from this installation, so a device that cannot reach GitHub can still be
    /// brought up to date from one that can (or from any device that is already newer).
    /// </summary>
    public async Task<string> SendOwnBuildAsync(string peerId, string peerName, CancellationToken ct = default)
    {
        var version = DeviceInfoService.LocalVersion;
        var theirs = _devices.PeerVersion(peerId);
        if (theirs is not null && theirs >= version)
        {
            return $"На {peerName} уже {theirs.ToString(3)}, обновлять нечего.";
        }

        string zip;
        try
        {
            zip = await Task.Run(() => PackOwnBuild(version), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Packing this build failed");
            return $"Не удалось упаковать эту сборку: {ex.Message}";
        }

        try
        {
            _logger.LogInformation("Offering build {Version} ({Size} MB) to {Peer}", version, new FileInfo(zip).Length / (1024 * 1024), peerName);
            var result = await _clipboard.SendFilesAsync(peerId, [zip], SendFilesPurpose.Update, version.ToString(3), ct);
            return result.Ok
                ? $"{peerName} обновляется до {version.ToString(3)}."
                : $"{peerName} не обновлён: {result.Error}";
        }
        finally
        {
            TryDelete(zip);
        }
    }

    /// <summary>Zips this installation (the layout the update script expects) into a temporary file.</summary>
    private static string PackOwnBuild(Version version)
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        var zip = Path.Combine(Path.GetTempPath(), $"WinMirroring-{version.ToString(3)}-{Environment.MachineName}.zip");
        TryDelete(zip);

        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        var prefix = source.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in source.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var relative = file.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? file.FullName[prefix.Length..] : file.Name;
            try
            {
                // The app's own files are open while it runs, but they stay readable.
                using var input = file.OpenRead();
                using var output = archive.CreateEntry(relative, CompressionLevel.Fastest).Open();
                input.CopyTo(output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read now (a log being written, say) is not part of the build.
            }
        }

        return zip;
    }

    // ---- receiving ---------------------------------------------------------------------------------------

    private void OnFilesOffered(PeerSession session, SendFilesRequest request) =>
        _ = request.Purpose == SendFilesPurpose.Update
            ? TakeUpdateAsync(session, request)
            : TakeToOpenAsync(session, request);

    private async Task TakeToOpenAsync(PeerSession session, SendFilesRequest request)
    {
        var what = request.Note is { Length: > 0 } note ? note : Describe(request);
        if (!await AskAsync($"Файлы с {session.PeerName}", $"{session.PeerName} отправил сюда «{what}» ({Size(request.Manifest.TotalSize)}). Принять и открыть?"))
        {
            _clipboard.DeclineFiles(session, request, "пользователь отказался");
            return;
        }

        var target = Path.Combine(ReceivedDirectory, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture));
        var paths = await _clipboard.AcceptFilesAsync(session, request, target);
        if (paths is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            Open(path);
        }

        Notify?.Invoke($"Открыто с {session.PeerName}", $"«{what}» сохранено в {target}.");
    }

    private async Task TakeUpdateAsync(PeerSession session, SendFilesRequest request)
    {
        var offered = Version.TryParse(request.Note, out var parsed) ? parsed : null;
        var mine = DeviceInfoService.LocalVersion;
        if (offered is not null && offered <= mine)
        {
            _clipboard.DeclineFiles(session, request, $"здесь уже {mine.ToString(3)}");
            return;
        }

        var offeredText = offered?.ToString(3) ?? "новую версию";
        if (!await AskAsync(
            "Обновление с другого устройства",
            $"{session.PeerName} предлагает обновить WinMirroring до {offeredText} (здесь {mine.ToString(3)}, {Size(request.Manifest.TotalSize)}).\n\n" +
            "Приложение закроется и запустится заново уже обновлённым. Устанавливать?"))
        {
            _clipboard.DeclineFiles(session, request, "пользователь отказался");
            return;
        }

        var target = Path.Combine(Path.GetTempPath(), "WinMirroring-update-" + Guid.NewGuid().ToString("N"));
        var paths = await _clipboard.AcceptFilesAsync(session, request, target);
        var zip = paths?.FirstOrDefault(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (zip is null)
        {
            Notify?.Invoke("Обновление не установлено", $"С {session.PeerName} не удалось получить сборку.");
            return;
        }

        _logger.LogInformation("Installing build {Version} received from {Peer}", offeredText, session.PeerName);
        if (!_updater.Apply(zip))
        {
            Notify?.Invoke("Обновление не установлено", "Не удалось запустить update.ps1 рядом с приложением.");
        }
    }

    private async Task<bool> AskAsync(string title, string text)
    {
        if (Confirm is not { } confirm)
        {
            return false;
        }

        try
        {
            return await confirm(title, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Asking about files from another device failed");
            return false;
        }
    }

    private static string Describe(SendFilesRequest request)
    {
        var top = request.Manifest.TopLevel.ToList();
        return top.Count == 1 ? top[0].Path : $"{top.Count} элемент(ов)";
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:F0} КБ"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):F1} МБ"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ"),
    };

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("Could not open {Path}: {Message}", path, ex.Message);
            Notify?.Invoke("Файл получен", $"«{Path.GetFileName(path)}» сохранён, но открыть его не удалось: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
