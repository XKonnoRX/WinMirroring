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
    private readonly WinMirroring.Shell.ShellService _shell;

    /// <summary>
    /// Whether a paired device may open a console here. Off until it is turned on: a paired device can already move
    /// this one's mouse and type on its keyboard, so this grants nothing new — but it makes that power convenient,
    /// and the first console each device opens is still confirmed by whoever is sitting here.
    /// </summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool AllowRemoteCommands { get; set; }

    partial void OnAllowRemoteCommandsChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _node.Settings.Shell.AllowCommands = value;
        _node.SaveSettings();

        // Switched off, it ends the consoles that are open and forgets the yes already given: it used to stop only
        // the next one from opening.
        _shell.ApplySettings();
        Say(value
            ? "Другие устройства могут выполнять здесь команды — при первом разрешении вы его подтвердите."
            : "Выполнение команд с других устройств выключено.");
    }

    /// <summary>
    /// Whether a paired device may ask for a console with administrator rights here. Its own switch, and off until
    /// it is turned on, because this one really is new power: an ordinary console can do what the person sitting
    /// here can do, and this one can do anything to the machine. It is confirmed here, and Windows asks as well
    /// when the helper that runs it starts — each of those once per run of the app, since the helper then keeps
    /// running and the prompt was a question about that program.
    /// </summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial bool AllowElevatedRemoteCommands { get; set; }

    partial void OnAllowElevatedRemoteCommandsChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _node.Settings.Shell.AllowElevatedCommands = value;
        _node.SaveSettings();
        _shell.ApplySettings();
        Say(value
            ? "Другие устройства могут просить здесь консоль администратора — вы подтвердите это один раз, и Windows спросит один раз, до перезапуска приложения."
            : "Команды с правами администратора с других устройств выключены.");
    }

    /// <summary>
    /// When this device spends less to do the same job: never, while it runs on its battery, or always. What that
    /// costs is frames and a little freshness; what is free is not in here, it was simply done.
    /// </summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial string PowerSaving { get; set; } = nameof(WinMirroring.Core.Settings.PowerSaving.OnBattery);

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial int PowerFps { get; set; } = 30;

    partial void OnPowerSavingChanged(string value)
    {
        if (!_initialized || !Enum.TryParse<WinMirroring.Core.Settings.PowerSaving>(value, out var mode))
        {
            return;
        }

        _node.Settings.Power.Saving = mode;
        _node.SaveSettings();
        Say(mode switch
        {
            WinMirroring.Core.Settings.PowerSaving.Always => "Экономия заряда включена всегда.",
            WinMirroring.Core.Settings.PowerSaving.OnBattery => "Экономия заряда включается, когда устройство на батарее.",
            _ => "Экономия заряда выключена.",
        });
    }

    partial void OnPowerFpsChanged(int value)
    {
        if (_initialized && Math.Clamp(value, 1, 240) is var kept && kept != value)
        {
            PowerFps = kept; // out of range: the nearest value that is in range, which the page then shows
            return;
        }

        if (!_initialized || value is < 1 or > 240)
        {
            return;
        }

        _node.Settings.Power.Fps = value;
        _node.SaveSettings();
    }

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

    /// <summary>
    /// The device's shared folders: checked that file sharing answers there, signed in to (with the Windows sign-in
    /// dialog when what this account has is not enough), and only then opened. See <see cref="Services.NetworkShare"/>.
    /// </summary>
    public async Task<string> OpenShareOfAsync(string peerId)
    {
        var name = NameOf(peerId);
        if (HostOf(peerId) is not { Length: > 0 } host)
        {
            return Say($"Адрес {name} пока неизвестен.");
        }

        Say($@"Подключаюсь к общим папкам {name} (\\{host})…");
        Services.NetworkShare.Result result;
        try
        {
            result = await Services.NetworkShare.OpenAsync(host, GetForegroundWindow()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogInformation("Share of {Peer} not opened: {Message}", name, ex.Message);
            return Say($@"Не удалось открыть \\{host}: {ex.Message}");
        }

        _logger.LogInformation("Share of {Peer} at {Unc}: {Outcome} ({Error})", name, result.Unc, result.Outcome, result.Error);
        return Say(result.Outcome switch
        {
            Services.NetworkShare.Outcome.Opened when result.Shares is { Count: 0 } =>
                $@"{result.Unc} открыт, но на {name} нет общих папок. Откройте там доступ к нужной папке (Свойства → Доступ) или зайдите в {result.Unc}\C$ под администратором того устройства.",
            Services.NetworkShare.Outcome.Opened => $"Открыт {result.Unc}.",
            Services.NetworkShare.Outcome.Unreachable =>
                $"{name} не отвечает по SMB (порт 445): там выключен «Общий доступ к файлам и принтерам» для этой сети или его закрывает брандмауэр.",
            Services.NetworkShare.Outcome.Cancelled => "Вход в сетевую папку отменён.",
            Services.NetworkShare.Outcome.Refused =>
                $"{name} не пустил с этими учётными данными (ошибка {result.Error}). Нужны имя и пароль учётной записи того устройства.",
            _ => $"Не удалось подключиться к {result.Unc}: {new System.ComponentModel.Win32Exception(result.Error).Message}",
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public string RemoteDesktopTo(string peerId) =>
        WithHost(peerId, host => Launch("mstsc.exe", $"/v:{host}", "удалённый рабочий стол") ?? Say($"Открываю удалённый рабочий стол {host}…"));

    /// <summary>
    /// A console on the device itself — the one tool here that does something on the other machine rather than
    /// something on this one with its address. In the place of logging into it: the session is already there and
    /// already authenticated, so there is nothing to connect and no password to have.
    /// </summary>
    public string OpenConsoleOn(string peerId, bool elevated = false)
    {
        var name = NameOf(peerId);
        if (_shell.Open(peerId, elevated) is not { } console)
        {
            return Say($"Консоль на {name} не открыть: нет готового соединения.");
        }

        var window = new Views.RemoteConsoleWindow(console);
        window.Show();
        return Say(elevated
            ? $"Прошу на {name} консоль администратора — если это здесь впервые за запуск приложения, там подтвердят её и ответят на запрос Windows…"
            : $"Открываю консоль на {name}…");
    }

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

    /// <summary>Whether there is anything to wake this device with: its hardware address, learnt while it was up on the local network.</summary>
    public bool CanWake(string peerId) =>
        Core.Network.WakeOnLan.TryParse(_node.Settings.FindPairedById(peerId)?.HardwareAddress, out _);

    /// <summary>
    /// Sends the device the packet its network adapter wakes it on. Nothing comes back from that: whether it woke
    /// is seen when it turns up on the network again, and connects by itself as a paired device does.
    /// </summary>
    public async Task<string> WakeAsync(string peerId)
    {
        var name = NameOf(peerId);
        var record = _node.Settings.FindPairedById(peerId);
        if (record is null || !Core.Network.WakeOnLan.TryParse(record.HardwareAddress, out var address))
        {
            return Say($"Чем будить {name}, пока неизвестно: адрес его сетевого адаптера запоминается, когда оно подключено по той же локальной сети (не через VPN).");
        }

        var shots = Core.Network.WakeOnLan.Shots(
            Core.Network.NetworkInterfaceCatalog.Enumerate(), record.WakeBroadcast, record.LastKnownAddresses);
        if (shots.Count == 0)
        {
            return Say($"Будить {name} не по чему: это устройство сейчас не в локальной сети.");
        }

        var went = await Core.Network.WakeOnLan.SendAsync(Core.Network.WakeOnLan.MagicPacket(address), shots).ConfigureAwait(true);
        Serilog.Log.Information("Wake packet for {Peer} ({Address}) sent {Went} of {Shots} ways", name, record.HardwareAddress, went, shots.Count);
        return Say(went > 0
            ? $"Пакет пробуждения для {name} отправлен ({record.HardwareAddress}). Если оно проснётся, подключится само — обычно за 5–20 секунд."
            : $"Пакет пробуждения для {name} отправить не удалось: сеть не приняла ни одной отправки.");
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
