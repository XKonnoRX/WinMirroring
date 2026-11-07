using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WinMirroring.App.Services;
using WinMirroring.Core;
using WinMirroring.Core.Discovery;
using WinMirroring.Core.Hosting;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Peers;
using WinMirroring.Core.Settings;
using WinMirroring.Input;

namespace WinMirroring.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int GridSize = 3;

    private readonly MirroringNode _node;
    private readonly InputSharingService _input;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, DeviceChoice> _choicesById = new(StringComparer.Ordinal);
    private readonly DeviceChoice _noDevice = new(string.Empty, "—");
    private readonly DispatcherTimer _statsTimer;
    private readonly bool _initialized;

    public MainViewModel(
        MirroringNode node,
        InputSharingService input,
        WinMirroring.Clipboard.ClipboardSyncService clipboard,
        WinMirroring.Audio.AudioService audio,
        WinMirroring.Screen.ScreenStreamingService screen,
        ILogger<MainViewModel> logger)
    {
        _node = node;
        _input = input;
        _clipboard = clipboard;
        _audio = audio;
        _screen = screen;
        _logger = logger;
        _dispatcher = Application.Current.Dispatcher;

        DeviceName = node.Settings.DeviceName;
        DeviceId = node.Identity.Id;
        Fingerprint = CertificateFingerprint.ToDisplay(node.Identity.Fingerprint);
        DiscoveryPort = node.Settings.DiscoveryPort;
        TransportPort = node.Settings.TransportPort;
        StartWithWindows = AutostartRegistration.IsEnabled();
        InputEnabled = node.Settings.Input.Enabled;
        EdgeResistance = node.Settings.Input.EdgeResistance;
        SmoothingEnabled = node.Settings.Input.SmoothingEnabled;
        MaxSmoothingDelay = node.Settings.Input.MaxSmoothingDelayMs;

        foreach (var address in node.Settings.ManualPeerAddresses)
        {
            ManualAddresses.Add(address);
        }

        DeviceChoices.Add(_noDevice);
        EnsureChoice(node.Identity.Id, $"{node.Settings.DeviceName} (это устройство)");
        for (var row = 0; row < GridSize; row++)
        {
            for (var column = 0; column < GridSize; column++)
            {
                LayoutCells.Add(new LayoutCellViewModel(column, row) { Selected = _noDevice });
            }
        }

        foreach (var peer in node.Peers.Snapshot())
        {
            Upsert(peer);
        }

        LoadLayout();
        UpdateInputFocus(input.RemoteTargetId);

        node.Peers.Changed += OnPeerChanged;
        input.FocusChanged += peerId => _dispatcher.InvokeAsync(() => UpdateInputFocus(peerId));
        input.LayoutChanged += () => _dispatcher.InvokeAsync(LoadLayout);

        LoadInterfaces();
        UpdateStatus();

        InitializeMedia();
        InitializeScreen();

        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RefreshStats(), _dispatcher);
        _statsTimer.Start();
        _initialized = true;
    }

    public ObservableCollection<PeerItemViewModel> Peers { get; } = [];

    public ObservableCollection<InterfaceItemViewModel> Interfaces { get; } = [];

    public ObservableCollection<string> ManualAddresses { get; } = [];

    public ObservableCollection<DeviceChoice> DeviceChoices { get; } = [];

    public ObservableCollection<LayoutCellViewModel> LayoutCells { get; } = [];

    public string DeviceId { get; }

    public string Fingerprint { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpairCommand))]
    public partial PeerItemViewModel? SelectedPeer { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PairCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnpairCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyNetworkCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string DeviceName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int DiscoveryPort { get; set; }

    [ObservableProperty]
    public partial int TransportPort { get; set; }

    [ObservableProperty]
    public partial string NewManualAddress { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedManualAddress { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NetworkSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InputFocusText { get; set; } = "Ввод: здесь";

    [ObservableProperty]
    public partial bool InputEnabled { get; set; }

    [ObservableProperty]
    public partial int EdgeResistance { get; set; }

    [ObservableProperty]
    public partial bool SmoothingEnabled { get; set; }

    [ObservableProperty]
    public partial int MaxSmoothingDelay { get; set; }

    [ObservableProperty]
    public partial string SmoothingText { get; set; } = string.Empty;

    partial void OnSmoothingEnabledChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        _node.Settings.Input.SmoothingEnabled = value;
        _node.SaveSettings();
        _input.ApplyInputSettings();
    }

    partial void OnMaxSmoothingDelayChanged(int value)
    {
        if (!_initialized || value is < 0 or > 200)
        {
            return;
        }

        _node.Settings.Input.MaxSmoothingDelayMs = value;
        _node.SaveSettings();
        _input.ApplyInputSettings();
    }

    // ---- devices -------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanPair))]
    private async Task PairAsync()
    {
        var peer = SelectedPeer;
        if (peer is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = $"Сопряжение с {peer.Name}: подтвердите запрос на другом устройстве и введите PIN.";
        try
        {
            var ok = await _node.PairAsync(peer.Id);
            StatusText = ok ? $"Сопряжено с {peer.Name}." : $"Сопряжение с {peer.Name} не выполнено.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pairing with {Peer} failed", peer.Name);
            StatusText = $"Сопряжение с {peer.Name} не удалось: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPair() => !IsBusy && SelectedPeer is { IsPaired: false, HasAddress: true };

    [RelayCommand(CanExecute = nameof(CanUnpair))]
    private async Task UnpairAsync()
    {
        var peer = SelectedPeer;
        if (peer is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Разорвать сопряжение с {peer.Name}? Для повторного подключения понадобится новый PIN.",
            "WinMirroring",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _node.UnpairAsync(peer.Id);
            StatusText = $"Сопряжение с {peer.Name} разорвано.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUnpair() => !IsBusy && SelectedPeer is { IsPaired: true };

    // ---- layout and input ----------------------------------------------------------------------------------

    [RelayCommand]
    private void SaveLayout()
    {
        var cells = LayoutCells
            .Where(c => c.Selected is { IsEmpty: false })
            .Select(c => new LayoutCell(c.Selected!.Id, c.Column, c.Row))
            .ToList();

        var duplicates = cells.GroupBy(c => c.DeviceId).Where(g => g.Count() > 1).Select(g => _choicesById[g.Key].Name).ToList();
        if (duplicates.Count > 0)
        {
            StatusText = $"Устройство не может стоять в двух ячейках: {string.Join(", ", duplicates)}.";
            return;
        }

        _input.ApplyLayout(cells);
        StatusText = cells.Any(c => c.DeviceId == _node.Identity.Id)
            ? "Раскладка сохранена и отправлена сопряжённым устройствам."
            : "Раскладка сохранена, но это устройство в ней не стоит: его курсор никуда не перейдёт.";
    }

    [RelayCommand]
    private void ReturnHome()
    {
        _input.ReturnHome();
        StatusText = "Ввод возвращён на это устройство.";
    }

    partial void OnInputEnabledChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            _input.SetEnabled(value);
            StatusText = value ? "Общий ввод включён." : "Общий ввод выключен.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling input sharing failed");
            StatusText = $"Не удалось изменить общий ввод: {ex.Message}";
        }
    }

    partial void OnEdgeResistanceChanged(int value)
    {
        if (!_initialized || value is < 1 or > 500)
        {
            return;
        }

        _node.Settings.Input.EdgeResistance = value;
        _node.SaveSettings();
    }

    private void UpdateInputFocus(string? peerId)
    {
        if (peerId is null)
        {
            InputFocusText = "Ввод: здесь";
            return;
        }

        var name = _choicesById.TryGetValue(peerId, out var choice) ? choice.Name : peerId[..Math.Min(8, peerId.Length)];
        InputFocusText = $"Ввод: на {name} (Ctrl+Alt+Home вернёт)";
    }

    private void LoadLayout()
    {
        var layout = _input.Layout;
        foreach (var cell in LayoutCells)
        {
            var match = layout.Cells.FirstOrDefault(c => c.Column == cell.Column && c.Row == cell.Row);
            cell.Selected = match is null ? _noDevice : EnsureChoice(match.DeviceId, null);
        }
    }

    private DeviceChoice EnsureChoice(string id, string? name)
    {
        if (_choicesById.TryGetValue(id, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(name) && id != _node.Identity.Id)
            {
                existing.Name = name;
            }

            return existing;
        }

        var choice = new DeviceChoice(id, string.IsNullOrWhiteSpace(name) ? id[..Math.Min(8, id.Length)] : name);
        _choicesById[id] = choice;
        DeviceChoices.Add(choice);
        return choice;
    }

    // ---- network -------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanApplyNetwork))]
    private async Task ApplyNetworkAsync()
    {
        IsBusy = true;
        try
        {
            var settings = _node.Settings;
            settings.DeviceName = string.IsNullOrWhiteSpace(DeviceName) ? Environment.MachineName : DeviceName.Trim();
            settings.DiscoveryPort = ClampPort(DiscoveryPort, settings.DiscoveryPort);
            settings.TransportPort = ClampPort(TransportPort, settings.TransportPort);
            settings.SelectedInterfaceIds = Interfaces.Where(i => i.IsSelected).Select(i => i.Id).Distinct().ToList();
            settings.ManualPeerAddresses = ManualAddresses.ToList();
            _node.SaveSettings();

            await _node.RestartNetworkAsync();

            DeviceName = settings.DeviceName;
            DiscoveryPort = settings.DiscoveryPort;
            TransportPort = settings.TransportPort;
            if (_choicesById.TryGetValue(_node.Identity.Id, out var self))
            {
                self.Name = $"{settings.DeviceName} (это устройство)";
            }

            LoadInterfaces();
            UpdateStatus();
            StatusText = "Сетевые настройки применены.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying network settings failed");
            StatusText = $"Не удалось применить настройки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApplyNetwork() => !IsBusy;

    [RelayCommand]
    private void RefreshInterfaces() => LoadInterfaces();

    [RelayCommand]
    private void AddManualAddress()
    {
        if (!ManualAddress.TryParse(NewManualAddress, DiscoveryPort, out _, out _))
        {
            StatusText = "Адрес должен быть вида host или host:порт.";
            return;
        }

        var value = NewManualAddress.Trim();
        if (!ManualAddresses.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            ManualAddresses.Add(value);
        }

        NewManualAddress = string.Empty;
        StatusText = "Адрес добавлен. Нажмите «Применить», чтобы начать опрос.";
    }

    [RelayCommand]
    private void RemoveManualAddress(string? address)
    {
        address ??= SelectedManualAddress;
        if (address is not null)
        {
            ManualAddresses.Remove(address);
        }
    }

    [RelayCommand]
    private async Task AddFirewallRulesAsync()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            return;
        }

        StatusText = "Подтвердите запрос контроля учётных записей…";
        var ok = await FirewallRules.AddAsync(executable, _node.Settings.TransportPort, _node.Settings.DiscoveryPort);
        StatusText = ok
            ? "Правила брандмауэра для частных сетей добавлены."
            : "Правила брандмауэра не добавлены (запрос отклонён или ошибка).";
    }

    // ---- settings ------------------------------------------------------------------------------------------

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            AutostartRegistration.Set(value);
            _node.Settings.StartWithWindows = value;
            _node.SaveSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autostart change failed");
            StatusText = $"Не удалось изменить автозапуск: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        var directory = Path.Combine(_node.SettingsStore.DataDirectory, "logs");
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private void LoadInterfaces()
    {
        var selectedIds = _node.Settings.SelectedInterfaceIds;
        Interfaces.Clear();
        foreach (var nic in Core.Network.NetworkInterfaceCatalog.Enumerate())
        {
            var selected = selectedIds.Count == 0 || selectedIds.Contains(nic.Id, StringComparer.OrdinalIgnoreCase);
            Interfaces.Add(new InterfaceItemViewModel(nic, selected));
        }
    }

    private void UpdateStatus()
    {
        var listening = _node.ListeningEndPoints.Count == 0
            ? "не слушает (нет выбранных интерфейсов)"
            : string.Join(", ", _node.ListeningEndPoints);
        NetworkSummary = $"TCP: {listening}; обнаружение: UDP {_node.Settings.DiscoveryPort}";
    }

    private void OnPeerChanged(PeerInfo info) => _dispatcher.InvokeAsync(() => Upsert(info));

    private void Upsert(PeerInfo info)
    {
        var existing = Peers.FirstOrDefault(p => p.Id == info.Id);
        if (existing is null)
        {
            Peers.Add(new PeerItemViewModel(info));
        }
        else
        {
            existing.Update(info);
        }

        EnsureChoice(info.Id, info.Name);
        RefreshPeerCommands();
    }

    private static int ClampPort(int requested, int fallback) =>
        requested is > 0 and <= ushort.MaxValue ? requested : fallback;
}
