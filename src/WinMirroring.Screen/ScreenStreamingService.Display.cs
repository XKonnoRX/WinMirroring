using System.Collections.Concurrent;
using System.Drawing;
using Microsoft.Extensions.Logging;
using WinMirroring.Core.Messages;
using WinMirroring.Core.Peers;
using WinMirroring.Screen.Display;

namespace WinMirroring.Screen;

/// <summary>
/// Second monitor: this device (the host) extends its desktop onto another device's screen through the Virtual
/// Display Driver's monitor. Windows dragged onto that monitor really leave the host's screens, and the other device
/// shows it full screen with touch. The host keeps a single virtual monitor, so one device at a time.
/// </summary>
public sealed partial class ScreenStreamingService
{
    private static readonly TimeSpan DisplayRequestLifetime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _displayGate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _displayRequested = new(StringComparer.Ordinal);
    private readonly HashSet<(int Width, int Height)> _modesAttempted = [];
    private string? _displayDevice;

    /// <summary>
    /// Set by the app: asks for administrator rights to add a resolution to the driver (true when added). Without
    /// it the closest offered resolution is used.
    /// </summary>
    public Func<int, int, Task<bool>>? AddDisplayModeAsync { get; set; }

    /// <summary>Side of this device's screen the device with that id sits on, from the layout ("right" by default).</summary>
    public Func<string, string>? SideOf { get; set; }

    /// <summary>Host side: the second monitor started or stopped (peer id or null).</summary>
    public event Action<string?>? ExtendedChanged;

    /// <summary>Either side: extending did not work (peer name, reason in Russian).</summary>
    public event Action<string, string>? ExtendFailed;

    /// <summary>True when the Virtual Display Driver's monitor exists on this device.</summary>
    public static bool IsVirtualDisplayAvailable => VirtualDisplay.FindDeviceName() is not null;

    /// <summary>Peer this device's desktop is extended onto, if any.</summary>
    public string? ExtendedTo => _sources.Values.FirstOrDefault(s => s.Kind == StreamKind.Display)?.Session.PeerId;

    public bool IsExtendedTo(string peerId) => ExtendedTo == peerId;

    /// <summary>Host: ask <paramref name="peerId"/> to become this device's second monitor.</summary>
    public async Task<bool> InviteAsDisplayAsync(string peerId)
    {
        if (!TryGetReady(peerId, out var session))
        {
            return false;
        }

        try
        {
            await session.SendControlAsync(new DisplayInviteMessage()).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogInformation("Display invite to {Peer} failed: {Message}", session.PeerName, ex.Message);
            return false;
        }
    }

    /// <summary>Device: ask <paramref name="peerId"/> to extend its desktop onto this device's screen.</summary>
    public async Task<bool> RequestExtendAsync(string peerId)
    {
        if (!TryGetReady(peerId, out var session))
        {
            return false;
        }

        var (width, height, scale) = VirtualDisplay.PrimaryScreen();
        _displayRequested[peerId] = Environment.TickCount64;
        try
        {
            await session.SendControlAsync(new DisplayExtendRequestMessage(width, height, scale)).ConfigureAwait(false);
            _logger.LogInformation("Asked {Peer} to extend onto this screen ({Width}x{Height}, {Scale}%)", session.PeerName, width, height, scale);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _displayRequested.TryRemove(peerId, out _);
            _logger.LogInformation("Extend request to {Peer} failed: {Message}", session.PeerName, ex.Message);
            return false;
        }
    }

    /// <summary>Host: stop extending onto whoever shows the second monitor.</summary>
    public async Task StopExtendingAsync()
    {
        foreach (var source in _sources.Values.Where(s => s.Kind == StreamKind.Display).ToList())
        {
            await StopSourceAsync(source.StreamId, "второй монитор отключён").ConfigureAwait(false);
        }
    }

    /// <summary>At start-up: a virtual monitor left on the desktop (crash, reboot) is taken off it.</summary>
    public void DetachLeftoverDisplay()
    {
        if (VirtualDisplay.FindDeviceName() is { } device && VirtualDisplay.IsAttached(device))
        {
            _logger.LogInformation("Taking the unused virtual monitor {Device} off the desktop: {Result}", device, VirtualDisplay.Detach(device));
        }
    }

    private bool AcceptsDisplayFrom(string peerId) =>
        _displayRequested.TryRemove(peerId, out var askedAt) && Environment.TickCount64 - askedAt < DisplayRequestLifetime.TotalMilliseconds;

    private void OnDisplayMessage(PeerSession session, ControlMessage message)
    {
        switch (message)
        {
            case DisplayInviteMessage:
                _logger.LogInformation("{Peer} invites this device to be its second monitor", session.PeerName);
                _ = RequestExtendAsync(session.PeerId);
                break;

            case DisplayExtendRequestMessage request:
                _ = Task.Run(() => ExtendToAsync(session, request));
                break;

            case DisplayExtendResultMessage { Ok: false } result:
                _displayRequested.TryRemove(session.PeerId, out _);
                ExtendFailed?.Invoke(session.PeerName, result.Error ?? "неизвестная ошибка");
                break;
        }
    }

    private async Task ExtendToAsync(PeerSession session, DisplayExtendRequestMessage request)
    {
        string? error = null;
        await _displayGate.WaitAsync().ConfigureAwait(false);
        try
        {
            error = await TryExtendLockedAsync(session, request).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _logger.LogWarning(ex, "Extending onto {Peer} failed", session.PeerName);
            error = ex.Message;
        }
        finally
        {
            _displayGate.Release();
        }

        if (error is null)
        {
            ExtendedChanged?.Invoke(session.PeerId);
            return;
        }

        _logger.LogInformation("Not extending onto {Peer}: {Error}", session.PeerName, error);
        ExtendFailed?.Invoke(session.PeerName, error);
        try
        {
            await session.SendControlAsync(new DisplayExtendResultMessage(false, error)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Returns null when the second monitor is up, else the reason.</summary>
    private async Task<string?> TryExtendLockedAsync(PeerSession session, DisplayExtendRequestMessage request)
    {
        if (!_node.Settings.Screen.AllowViewing)
        {
            return "просмотр экрана запрещён на этом устройстве";
        }

        if (ExtendedTo is { } current)
        {
            return current == session.PeerId ? null : "виртуальный монитор уже показывается на другом устройстве";
        }

        var device = VirtualDisplay.FindDeviceName();
        if (device is null)
        {
            return "не установлен драйвер виртуального монитора (Virtual Display Driver)";
        }

        var width = Math.Clamp(request.Width, 640, 8192);
        var height = Math.Clamp(request.Height, 480, 8192);
        var mode = VirtualDisplay.PickMode(VirtualDisplay.Modes(device), width, height, out var exact);
        if (!exact && AddDisplayModeAsync is { } addMode && _modesAttempted.Add((width, height)))
        {
            _logger.LogInformation("Virtual monitor has no {Width}x{Height} mode; asking to add it", width, height);
            if (await addMode(width, height).ConfigureAwait(false))
            {
                // The device restarts to read the new mode; wait for its monitor to come back.
                device = await WaitForDeviceAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (device is null)
                {
                    return "виртуальный монитор не вернулся после перезапуска драйвера";
                }

                mode = VirtualDisplay.PickMode(VirtualDisplay.Modes(device), width, height, out exact);
            }
        }

        // Some drivers list no modes while their output is off the desktop: then try the screen's own size.
        var chosen = mode ?? new DisplayMode(width, height, 60);
        var side = SideOf?.Invoke(session.PeerId) ?? "right";
        var others = Capture.DisplayCatalog.Monitors().Where(m => !string.Equals(m.DeviceName, device, StringComparison.OrdinalIgnoreCase)).ToList();
        var position = VirtualDisplay.PlaceBeside(others, new Size(chosen.Width, chosen.Height), side);
        if (!VirtualDisplay.Attach(device, chosen, position, out var code))
        {
            _logger.LogInformation("Attaching {Device} failed ({Code}); trying the extend topology", device, code);
            if (!VirtualDisplay.ExtendTopology())
            {
                return $"Windows не подключил виртуальный монитор (код {code})";
            }

            await Task.Delay(1000).ConfigureAwait(false);
            if (mode is null && VirtualDisplay.PickMode(VirtualDisplay.Modes(device), width, height, out exact) is { } offered)
            {
                chosen = offered;
                position = VirtualDisplay.PlaceBeside(others, new Size(chosen.Width, chosen.Height), side);
            }

            if (!VirtualDisplay.Attach(device, chosen, position, out code))
            {
                _logger.LogInformation("Setting {Width}x{Height} at {Position} on {Device} failed ({Code}); keeping what Windows chose", chosen.Width, chosen.Height, position, device, code);
            }
        }

        _displayDevice = device;
        var monitor = await WaitForMonitorAsync(device, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (monitor is null)
        {
            VirtualDisplay.Detach(device);
            return "виртуальный монитор не появился на рабочем столе";
        }

        if (!VirtualDisplay.TrySetScale(device, request.ScalePercent))
        {
            _logger.LogInformation("Scale {Scale}% not applied to the virtual monitor; Windows keeps its own", request.ScalePercent);
        }

        _logger.LogInformation("Extending onto {Peer}: {Width}x{Height}@{Hz} at {Position}{Exact}", session.PeerName, chosen.Width, chosen.Height, chosen.Frequency, position, exact ? string.Empty : " (closest offered mode)");
        var source = ScreenSource.ForDisplay(session, NewStreamId(), monitor, _node.Settings.Screen, _logger);
        if (!await StartSourceAsync(session, source).ConfigureAwait(false))
        {
            VirtualDisplay.Detach(device);
            return "не удалось начать передачу изображения";
        }

        return null;
    }

    private static async Task<string?> WaitForDeviceAsync(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        await Task.Delay(1500).ConfigureAwait(false);
        while (Environment.TickCount64 < deadline)
        {
            if (VirtualDisplay.FindDeviceName() is { } device && VirtualDisplay.Modes(device).Count > 0)
            {
                return device;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<Capture.MonitorDescription?> WaitForMonitorAsync(string device, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (VirtualDisplay.FindMonitor(device) is { } monitor)
            {
                return monitor;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    /// <summary>Called whenever a source goes away: the virtual monitor leaves the desktop with its last stream.</summary>
    private void OnSourceRemoved(ScreenSource source)
    {
        if (source.Kind != StreamKind.Display)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _displayGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ExtendedTo is null && (_displayDevice ?? VirtualDisplay.FindDeviceName()) is { } device)
                {
                    _logger.LogInformation("Second monitor on {Peer} ended; detaching {Device}: {Result}", source.Session.PeerName, device, VirtualDisplay.Detach(device));
                }
            }
            finally
            {
                _displayGate.Release();
            }

            ExtendedChanged?.Invoke(ExtendedTo);
        });
    }
}
