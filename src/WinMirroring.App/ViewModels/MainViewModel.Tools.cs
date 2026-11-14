using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace WinMirroring.App.ViewModels;

/// <summary>
/// The small tools for one device: copy its address, open its shared folders, a remote desktop, a ping, measure
/// the link, fetch its log, drop and remake the connection. Nothing clever — ordinary Windows things done to a
/// device whose address we already know — but they are what one actually reaches for, and they were left behind
/// in the old window's context menu when the device panel replaced it.
///
/// They take a device id rather than reading whatever is selected, so the panel and the old menu both call the
/// same thing, and each returns what it did for the caller to show.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The address without a port, which is what all of these need; empty when it is not known.</summary>
    public string HostOf(string peerId) => Peers.FirstOrDefault(p => p.Id == peerId)?.Host ?? string.Empty;

    private string NameOf(string peerId) =>
        Peers.FirstOrDefault(p => p.Id == peerId)?.Name
        ?? (_node.Peers.TryGet(peerId, out var info) ? info.Name : peerId);

    public string CopyAddressOf(string peerId)
    {
        if (HostOf(peerId) is not { Length: > 0 } host)
        {
            return Say($"Адрес {NameOf(peerId)} пока неизвестен.");
        }

        try
        {
            System.Windows.Clipboard.SetText(host);
            return Say($"Адрес {host} скопирован.");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            return Say($"Буфер обмена занят: {ex.Message}");
        }
    }

    public string OpenShareOf(string peerId) =>
        WithHost(peerId, host => Launch("explorer.exe", $@"\\{host}", "сетевую папку") ?? Say($"Открываю \\\\{host}…"));

    public string RemoteDesktopTo(string peerId) =>
        WithHost(peerId, host => Launch("mstsc.exe", $"/v:{host}", "удалённый рабочий стол") ?? Say($"Открываю удалённый рабочий стол {host}…"));

    public string PingTo(string peerId) =>
        WithHost(peerId, host => Launch("cmd.exe", $"/k ping -t {host}", $"ping {host}") ?? Say($"Ping {host} запущен в консоли."));

    /// <summary>Round trips over the session itself, which works where ICMP is blocked.</summary>
    public async Task<string> MeasureLatencyToAsync(string peerId)
    {
        var name = NameOf(peerId);
        if (!_node.Sessions.TryGetSession(peerId, out var session))
        {
            return Say($"С {name} нет соединения — задержку мерить не по чему.");
        }

        Say($"Измеряю задержку до {name}…");
        var samples = new List<double>(LatencyProbeCount);
        try
        {
            for (var i = 0; i < LatencyProbeCount; i++)
            {
                if (await session.PingAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true) is { } rtt)
                {
                    samples.Add(rtt.TotalMilliseconds);
                }

                await Task.Delay(50).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            return Say($"Измерение прервано: соединение с {name} закрыто.");
        }

        if (samples.Count == 0)
        {
            return Say($"{name} не ответил ни на один из {LatencyProbeCount} запросов.");
        }

        var jitter = samples.Count > 1
            ? samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).Average()
            : 0;
        if (Peers.FirstOrDefault(p => p.Id == peerId) is { } item)
        {
            item.RttText = $"{samples.Average():F1} мс";
        }

        return Say($"{name}: в среднем {samples.Average():F1} мс (мин {samples.Min():F1}, макс {samples.Max():F1}), "
            + $"разброс {jitter:F1} мс, потеряно {LatencyProbeCount - samples.Count} из {LatencyProbeCount}.");
    }

    public async Task<string> FetchLogOfAsync(string peerId)
    {
        var name = NameOf(peerId);
        if (!_node.Sessions.TryGetSession(peerId, out _))
        {
            return Say($"Журнал {name} не получить: нет соединения.");
        }

        Say($"Запрашиваю журнал {name}…");
        try
        {
            var path = await _diagnostics.FetchLogAsync(peerId).ConfigureAwait(true);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return Say($"Журнал {name} сохранён: {path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            _logger.LogInformation("Log of {Peer} not fetched: {Message}", name, ex.Message);
            return Say($"Журнал {name} не получен: {ex.Message}");
        }
    }

    public async Task<string> ReconnectToAsync(string peerId)
    {
        var name = NameOf(peerId);
        if (!_node.Sessions.TryGetSession(peerId, out var session))
        {
            return Say($"С {name} и так нет соединения.");
        }

        var paired = Peers.FirstOrDefault(p => p.Id == peerId)?.IsPaired ?? false;
        await session.CloseAsync("reconnect requested").ConfigureAwait(true);
        return Say(paired
            ? $"Соединение с {name} закрыто, переподключение произойдёт автоматически."
            : $"Соединение с {name} закрыто.");
    }

    private string WithHost(string peerId, Func<string, string> act) =>
        HostOf(peerId) is { Length: > 0 } host ? act(host) : Say($"Адрес {NameOf(peerId)} пока неизвестен.");

    /// <summary>Puts a line in the status bar and hands the same line back to whoever asked.</summary>
    private string Say(string message)
    {
        StatusText = message;
        return message;
    }
}
