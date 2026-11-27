# Приложение: запуск, трей, обновление, мост к интерфейсу (WinMirroring.App)

> Часть справочника [docs/FEATURES.md](../FEATURES.md). Составлено по коду версии 1.0.75; источник правды — код.

Источник: `src/WinMirroring.App/App.xaml.cs`, `App.Resume.cs`, `App.Viewers.cs`, `Services/*`, `ViewModels/*`, `WinMirroring.App.csproj`, `app.manifest`, `scripts/update.ps1`. Не разобраны здесь (их ведут другие разделы): `ButtonBarService`, `IViewerControl`, `SystemTouchGestures`, папка `Views`, React-страницы. Пути ниже — от корня репозитория.

### Состав и сборка проекта

**Что делает.** Один WPF-процесс `WinMirroring.exe` на устройство: значок в трее, окна на React (WebView2), окна просмотра экрана, обновление, журнал.

**Как устроено.** `src/WinMirroring.App/WinMirroring.App.csproj`: `OutputType=WinExe`, `UseWPF`, `AssemblyName=WinMirroring`, `<Version>1.0.75</Version>` (её сравнивает автообновление; поднимается с каждым релизом). Ссылки на проекты Core, Input, Clipboard, Audio, Screen, Shell. Пакеты: CommunityToolkit.Mvvm 8.4.2, H.NotifyIcon.WPF 2.4.1, Microsoft.Extensions.DependencyInjection/Logging 10.0.14, Microsoft.Web.WebView2 1.0.4314.35, Serilog 4.4.0 (+ Extensions.Logging 10.0.0, Sinks.File 7.0.0). Цели MSBuild:
- `BuildUi` (перед сборкой, если не `SkipUiBuild=true`): `npm.cmd ci` (когда нет `node_modules`) и `npm.cmd run build` в `src/WinMirroring.Ui`; в PATH добавляется `%ProgramFiles%\nodejs`. Ошибка npm — только предупреждение.
- `CopyUi` / `PublishUi`: `src/WinMirroring.Ui/dist/**` → `wwwroot\` рядом с exe.
- `PublishInputHost` (после Publish): `dotnet publish` проекта `WinMirroring.InputHost` в ту же папку (RID по умолчанию `win-x64`, self-contained по умолчанию `true`).
- `scripts/update.ps1` и `scripts/update.cmd` копируются в каждую сборку и публикацию.

`app.manifest`: `requestedExecutionLevel=asInvoker`, `dpiAwareness=PerMonitorV2`, `longPathAware=true`, поддерживаемая ОС — Windows 10/11. `App.xaml`: `ShutdownMode="OnExplicitShutdown"`.

**Ограничения и запасные пути.** Без собранного `wwwroot\index.html` приложение работает со старыми WPF-окнами (`MainWindow`, `ControlPanelWindow`), а функция «Главное окно» помечается неисправной.

### Запуск: аргументы командной строки и единственный экземпляр

**Что делает.** Запускается одна копия на сеанс пользователя; вторая показывает сообщение и выходит. Есть служебные аргументы для обновления, перезапуска и установки драйвера.

**Как устроено.** `App.OnStartup` (`App.xaml.cs`) проверяет аргументы в таком порядке:

| Аргумент | Действие |
|---|---|
| `--vdd-add-mode <arg>` (`VirtualDisplaySetup.Argument`) | повышенный вспомогательный запуск: `VirtualDisplaySetup.Run(arg)`, код возврата = код выхода; без UI, журнала и блокировки |
| `--vdd-install <package> [reportPath]` (`VirtualDisplaySetup.InstallArgument`) | то же: `VirtualDisplaySetup.Install(...)` |
| `--exit` | открывает событие `Local\WinMirroring.App.Exit`, взводит его (работающая копия штатно закрывается) и выходит |
| `--restarted` | ждать мьютекс до 20 с (старая копия ещё закрывается); без него ожидание 0 |
| `--minimized` | не открывать главное окно после запуска (только трей и плавающая кнопка) |

Единственный экземпляр — именованный мьютекс `Local\WinMirroring.App.SingleInstance`; `AbandonedMutexException` считается владением. Не получили — `MessageBox` «WinMirroring уже запущен. Ищите значок в области уведомлений.» и `Shutdown()`.

**Ограничения и запасные пути.** Пространство `Local\` — блокировка и сигнал выхода действуют в пределах одного сеанса Windows. В служебных режимах `--vdd-*` Serilog не настроен, вызовы `Log.*` никуда не пишут.

### Контейнер служб и порядок запуска

**Что делает.** Каждая функция стартует отдельно; сбой одной не роняет остальные, а её состояние видно в разделе диагностики и в уведомлении «WinMirroring: не всё запустилось».

**Как устроено.** `App.BuildServices` регистрирует одиночками: `ILoggerFactory` (Serilog), `DialogPairingUi` (он же `IPairingUserInterface`), `FeatureHealth`, `DiagnosticsService`, `MirroringNode` (`new NodeOptions()` — каталог данных по умолчанию), `InputSharingService`, `ElevatedInjector`, `ClipboardSyncService`, `AudioService`, `DragDropCoordinator`, `ScreenStreamingService`, `ShellService`, `LocalShellServer`, `GithubUpdater`, `ButtonBarService`, `DeviceInfoService`, `PeerFilesService`, `UiBridge`, `HubUi`, `SettingsUi`, `RemoteGuard`, `RemoteKeyboardWatcher`, `IViewerControl` (сам `App`), `PanelUi`, `MainViewModel`.

Порядок в `OnStartup` после получения мьютекса: `ConfigureLogging` → `ListenForExitSignal` → обработчики `DispatcherUnhandledException` (журнал + `MessageBox` «WinMirroring: ошибка», `Handled=true`; с 1.0.78 — не больше одного окна об ошибке одновременно, а смерть потока отрисовки WPF — `OutOfMemoryException` из `DUCE.Channel…` или `UCEERR_RENDERTHREADFAILURE` — не показывается окном, а ведёт к перезапуску: `RestartAfterDrawingDied` запускает новый процесс с прежними аргументами и `--restarted` и выходит обычным путём) и `TaskScheduler.UnobservedTaskException` (Warning в журнал) → `BuildServices` → подписка на `Sessions.UnpairedByPeer` (уведомление «Сопряжение разорвано») → `await node.StartAsync()` → функции через `FeatureHealth.Start(name, action)` (`Services/FeatureHealth.cs`: исключение → `Ok=false`, `Detail="{Тип}: {сообщение}"`, запись Error в журнал):

1. «Сеть» — `node.StartAsync()`.
2. «Общий ввод» — `input.Start()`; `MonitorCatalog.VirtualMonitorIsHidden = () => screen.ExtendedTo is null`; `SystemEvents.DisplaySettingsChanged → input.RefreshMonitors()`; уведомления `RemoteInputBlocked` и `KeyboardLayoutMissing`.
3. «Раскладка клавиатуры» — только строка со списком установленных раскладок (`KeyboardLayouts.Installed`).
4. «Буфер обмена» — `clipboard.Start`; `TransferProgress` → индикатор передачи.
5. «Файлы и версии устройств» — `DeviceInfoService.Start()`, `PeerFilesService` (`Confirm` = `MessageBox` Да/Нет, `Notify` = уведомление в трее).
6. «Выполнение команд» — `ShellService` (`Confirm` = `MessageBox` Да/Нет с предупреждением, `Notify`), затем `LocalShellServer.Start()`.
7. «Главное окно» — `HubUi.Register()`, `SettingsUi.Register()`, `RemoteKeyboardWatcher.Start()`, `PanelUi.Register()` (с делегатами `Hide`, `IsShown`, `OpenHub`, `OpenSettings`, `OpenButtonEditor`, `HideButton`); если `!WebWindow.IsAvailable` — исключение «нет собранного интерфейса в …»; иначе `WebWindow.WarmUp()`. `MainViewModel` создаётся здесь (зависимость `HubUi`).
8. «Перетаскивание файлов» — `DragDropCoordinator.Start()`; если `clipboard.Drag is null` — отметка о сбое (перенос окон при этом всё равно работает).
9. «Звук» — `audio.Start()`; `input.ControlledByChanged → audio.SetFocusTarget`; `screen.ViewersChanged → audio.SetViewers`.
10. «Экран и окна» — `screen.ViewerOpened → OpenViewerWindow`; `screen.SourcesChanged → UpdateKeepAwake`; `screen.Start()`; `screen.AttachWindowDrag(input)`.
11. «Второй монитор» — `screen.AddDisplayModeAsync` (уведомление + `VirtualDisplaySetup.AddModeElevatedAsync`), `screen.SideOf`, `input.IsCrossingSuppressed = screen.IsExtendedTo`, уведомления `ExtendFailed`/`ExtendedChanged`, `screen.DetachLeftoverDisplay()`; затем строка «драйвер виртуального монитора найден / не установлен…».
12. «Диагностика» — `DiagnosticsService.Start`.
13. «Управление окнами администратора» — подписка на `ElevatedInjector.StateChanged` (обновляет здоровье, `MainViewModel.SetAdminControlState`, уведомление); если `ElevatedInjector.HelperPath is null` — «вспомогательная программа WinMirroring.InputHost.exe не входит в эту сборку»; если `Settings.Input.ElevatedInjector` — `EnableAsync()`.

Дальше: `CreateTrayIcon()` → `CreateFloatingMenu(node)` → `DialogPairingUi.Notify = ShowNotification` → уведомление о сбоях → `ShowHub()` (если нет `--minimized`) → `StartUpdateChecks(node)`.

**Ограничения и запасные пути.** Уведомления, возникшие до `CreateTrayIcon`, теряются (`_trayIcon?.ShowNotification`).

### Завершение работы

**Что делает.** «Выход» в трее, сигнал от скрипта обновления или перезапуск от администратора закрывают приложение штатно: окна возвращаются, виртуальный монитор отсоединяется, динамики включаются. Зависший шаг не держит процесс.

**Как устроено.** `App.ExitAsync`: флаг `_exiting`; `StartExitWatchdog`; `CloseWindows()` (у `WebWindow` — `CloseForGood()`, у `MainWindow` — `AllowClose()`; закрываются индикатор, панель, плавающая кнопка, редактор кнопок, все просмотры; `_trayIcon.Dispose()`); затем шаги `StopStepAsync` — каждый в `Task.Run`, с пределом `StepTimeout = 4 с`, с записью длительности в журнал: drag and drop → screen streaming → elevated input helper → input sharing → sound → clipboard → network (`MirroringNode`) → services (`ServiceProvider.DisposeAsync`). В `finally`: `Log.CloseAndFlushAsync`, `ReleaseMutex`, `_exitFinished.Set()`, `Shutdown()`.
Сторож (`ExitBudget = 12 с`): если штатное завершение не уложилось — `Process.Kill()`; если уложилось — всё равно `Kill()` через 3 с после него.
Сигнал извне: `ListenForExitSignal` — фоновый поток ждёт `EventWaitHandle` `Local\WinMirroring.App.Exit` (AutoReset) и вызывает `ExitAsync`.
«Выход» из меню (`ExitFromMenuAsync`) сначала проходит `RemoteGuard.ConfirmAsync` («Закрыть WinMirroring на этом устройстве?»).

**Ограничения и запасные пути.** Закрытие окна (главного, панели, старых настроек) приложение не завершает — окно только прячется.

### Автозапуск с Windows

**Что делает.** Переключатель «запускать с Windows» запускает приложение при входе свёрнутым в трей.

**Как устроено.** `Services/AutostartRegistration.cs`: значение `WinMirroring` в `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` = `"<Environment.ProcessPath>" --minimized`. `IsEnabled()` — значение есть и содержит путь текущего exe. `MainViewModel.OnStartWithWindowsChanged` вызывает `Set` и пишет `AppSettings.StartWithWindows`.

**Настройки.** `AppSettings.StartWithWindows` (false). Начальное значение свойства `MainViewModel.StartWithWindows` берётся из реестра, не из настроек.

**Ограничения и запасные пути.** После переноса папки установки запись указывает на старый путь и `IsEnabled()` возвращает false. Ошибка записи в реестр — строка в `StatusText`.

### Права администратора: перезапуск, помощник, брандмауэр

**Что делает.** Приложение работает с обычными правами. Чтобы управлять окнами администратора, включается отдельный помощник `WinMirroring.InputHost.exe`; запасной вариант — перезапустить всё приложение от администратора. Правила брандмауэра добавляются кнопкой.

**Как устроено.**
- `App.RestartElevatedAsync`: `Process.Start(ProcessPath, "--restarted")` с `Verb="runas"`; отказ UAC (`Win32Exception`) → false и «Перезапуск с правами администратора отменён.»; иначе `ExitAsync()`. Команда `MainViewModel.RestartElevatedCommand`; `IsElevated` = `InjectionGuard.IsElevated`, `CanRestartElevated`, `ElevationText`.
- `App.SetAdminWindowControlAsync(bool)`: пишет `Settings.Input.ElevatedInjector`, вызывает `ElevatedInjector.EnableAsync()` / `Disable()`. Фактическое состояние возвращается событием `StateChanged` → `MainViewModel.SetAdminControlState` (флаг `_applyingAdminControl` не даёт зациклиться).
- Запуск помощника без запроса UAC: команды `settings.command` `adminNoPrompt` / `adminPrompt` → `ElevatedInjector.AllowStartingWithoutPrompting` / `RequirePromptAgain` (задание Планировщика, `ElevatedHelperTask`); в снимке — `input.adminNoPrompt`.
- Брандмауэр: `MainViewModel.AddFirewallRulesCommand` → `FirewallRules.AddAsync(exe, TransportPort, DiscoveryPort)` (`src/WinMirroring.Core/Hosting`): повышенный `cmd /c netsh …`, правила «WinMirroring (TCP in)» и «WinMirroring (UDP in)» для профилей private, domain.

**Настройки.** `InputSettings.ElevatedInjector` (false).

**Ограничения и запасные пути.** Перезапуск от администратора не сохраняет `--minimized`. Все эти действия, нажатые с другого устройства, сначала переспрашиваются (`RemoteGuard`).

### Значок в трее и его меню

**Что делает.** Значок «WinMirroring» в области уведомлений: левый щелчок открывает главное окно, правый — меню быстрых действий.

**Как устроено.** `App.CreateTrayIcon`: `H.NotifyIcon.TaskbarIcon`, значок `Assets/app.ico`, `TrayLeftMouseUp → ShowHub()`. Меню пересобирается при каждом открытии (`FillTrayMenu`):

| Пункт | Действие |
|---|---|
| «Открыть WinMirroring» (полужирный) | `ShowMainWindow()`: главное окно плюс событие `hub.openSettings` — окно открывается на вкладке «Параметры»; без `wwwroot` — старое окно настроек |
| «Вернуть курсор» | `MainViewModel.ReturnCursor()` → `InputSharingService.ReturnCursor()`; результат уведомлением |
| «Вернуть окна на место» | `MainViewModel.ReturnWindows()` → `ScreenStreamingService.RestoreMovedWindows()` |
| «Показать кнопку меню» | `ShowFloatingMenu()`: снимает `MenuButtonHidden`, показывает плавающую кнопку |
| «Звук этого устройства» → «Автоматически: за вводом и просмотром» | `SendSoundTo(null)`; отметка = `SoundAutomatic` |
| … → «Только здесь» | `KeepSoundHere()`; отметка = `SoundKeptHere` |
| … → «Всегда на {имя}» (по одному на готовое устройство) | `SendSoundTo(id, name)`; отметка = `IsSoundSentTo(id)` |
| «Слушать здесь звук устройства» → {имя} | `ToggleSoundHereAsync`; отметка = `IsSoundFrom(id)` |
| «Показать экран устройства» → {имя} | `ScreenStreamingService.RequestScreenAsync(id)` |
| «Использовать как второй монитор» → {имя} | `ToggleSecondMonitorAsync`; отметка = `IsSecondMonitor(id)` |
| «Стать вторым монитором для» → {имя} | `BecomeSecondMonitorAsync` |
| «Выход» | `ExitFromMenuAsync()` (с `RemoteGuard`) |

Четыре подменю по устройствам появляются, только когда есть хотя бы одно устройство в состоянии Ready (`MainViewModel.ReadyPeers`).

**Ограничения и запасные пути.** «Показать экран устройства» из трея вызывает `RequestScreenAsync` напрямую, минуя `ShowOrSwitchScreen` (правило «один просмотр на устройство» здесь не применяется). Из пунктов меню только «Выход» проходит `RemoteGuard`.

### Плавающая кнопка, боковая панель, главное окно

**Что делает.** Круглая кнопка поверх экрана открывает и закрывает боковую панель; её можно перетащить и скрыть. Главное окно — поле устройств и вкладка «Параметры».

**Как устроено.** `App.CreateFloatingMenu`: `Views.FloatingMenuWindow`; `Clicked → ToggleControlPanel`; `MovedTo` пишет `MenuButtonX/Y`. `PositionFloatingMenu`: при отрицательных координатах — `WorkArea.Right − 72`, `WorkArea.Top + 90`; иначе координаты зажимаются в рабочую область (запас 60). `HideFloatingMenu` прячет панели и кнопку, ставит `MenuButtonHidden=true`.
`ToggleControlPanel`: при наличии `wwwroot` — `WebWindow(bridge, "panel", panel: true) { KeepLoaded = true }`, `ShowDocked()` (ширина 360, правый край рабочей области, `Topmost`, без фокуса, исключена из захвата); иначе `ControlPanelWindow.Toggle(...)`.
`ShowHub`: одно окно `WebWindow(bridge, "hub") { KeepLoaded = true }`; повторный вызов показывает и активирует его. `ShowButtonEditor`: одно окно `ButtonPresetsWindow`, `CurrentProgram` = `ForegroundProcess` активного (или первого) просмотра. `KeepFloatingOnTop` перевзводит `Topmost` кнопки после показа просмотра или панели.

**Настройки.** `AppSettings.MenuButtonX`, `MenuButtonY` (−1 = угол по умолчанию), `MenuButtonHidden` (false).

**Ограничения и запасные пути.** Положение и размер главного окна нигде не сохраняются (в этом коде — нет; в `Views` — не выяснено).

### Окна React: WebWindow и UiBridge

**Что делает.** Страницы интерфейса — локальные файлы, показанные в WebView2; с приложением они общаются сообщениями, без веб-сервера.

**Как устроено.** `Views/WebWindow.xaml.cs`: `RootDirectory = <папка exe>\wwwroot`, `IsAvailable` = есть `index.html`. Одна среда WebView2 на все окна, данные в `%LocalAppData%\WinMirroring\webview`, язык `ru`. `SetVirtualHostNameToFolderMapping("winmirroring.local", wwwroot)`, навигация на `https://winmirroring.local/index.html#{route}` (маршруты `hub` и `panel`; `settings` и `layout` страница понимает как `hub`). Навигация за пределы хоста отменяется, новые окна запрещены, отключены контекстное меню, масштаб, жест «назад», строка состояния, клавиши браузера. `KeepLoaded`: закрытие только прячет окно; скрытому окну `View.Visibility = Hidden`, чтобы страница не рисовалась и не тикала. `WarmUp()` заранее создаёт среду.
`Services/UiBridge.cs`: страница шлёт `{id, method, args}` через `chrome.webview.postMessage`; ответ `{id, ok, result|error}`; события `{event, data}` (`Push`) уходят всем подключённым страницам. JSON — camelCase (`UiBridge.Json`). Методы регистрируются `On` / `OnAsync`; неизвестный метод → `ok=false`, «нет такого метода: …»; исключение обработчика → Warning в журнал и `error = ex.Message`. Всё на UI-потоке. События данных не несут — страница перечитывает снимок через `*.get`.

**Ограничения и запасные пути.** WebView2 не запустился — в окне текст «Не удалось открыть окно: WebView2 недоступен.». Нет `wwwroot` — старые окна.

### Мост HubUi (поле устройств)

**Что делает.** Даёт главному окну список устройств с мониторами и раскладкой, принимает сохранённую раскладку и команды над устройством.

**Как устроено.** `Services/HubUi.cs`.

| Метод | Аргументы | Результат |
|---|---|---|
| `hub.get` | — | снимок (ниже) |
| `hub.devices` | — | тот же снимок (страницы его не вызывают) |
| `hub.save` | `{placements:[{deviceId,x,y}]}` либо `{cells:[{deviceId,monitor,column,row}]}` | `InputSharingService.ApplyPlacements` / `ApplyLayout`; строка «Раскладка сохранена и отправлена устройствам.» |
| `hub.command` | `{name, deviceId?, monitor?, text?}` | строка для показа или null |

Событие `hub.changed` — при `LayoutChanged`, `MonitorsChanged`, `FocusChanged`, `DeviceInfoService.Changed`, `Introductions.Changed`, `Peers.Changed`, `SessionReady`, `SessionClosed`, `ScreenStreamingService.SourcesChanged`, после добавления или удаления адреса. Событие `hub.openSettings` шлёт `App.ShowMainWindow`.

Снимок: `self`, `inputFocus`, `ownSound`, `network`, `manualAddresses[]`, `cells[]`, `placements[]`, `devices[]`. Устройство (`DeviceDto`): `id`, `name`, `isThisDevice`, `paired`, `online`, `state`, `address`, `host`, `rtt`, `fingerprint`, `lastError`, `version`, `canUpdate`, `monitors[]` (`key`, `label`, `size`, `width`, `height`, `left`, `top`, `primary`), `hasVirtualDisplay`, `sound` (`here`: `always|never|auto`, `playing`, `sentTo`), `shownMonitor`, `introducedBy`. В список входят: это устройство, все из `node.Peers`, и известные только через сопряжённое устройство (`node.Introductions`, состояние «известно через …»). Для неподключённого устройства мониторы берутся из запомненных в раскладке (`KeptMonitors`).

Команды `hub.command` (звёздочка — с другого устройства переспрашивается через `RemoteGuard`):

| name | Что делает |
|---|---|
| `pair` | `node.PairAsync(id)` |
| `trust` | `node.Introductions.TrustAsync(id)` — сопряжение по поручительству доверенного устройства |
| `unpair` | `MessageBox` «Разорвать сопряжение с …?» → `node.UnpairAsync(id)` |
| `showScreen` | `IViewerControl.ShowOrSwitchScreen(id, index, monitorKey)`; индекс по ключу — `MonitorIndexOf` |
| `sendWindow` | `MainViewModel.SendWindowToPeerAsync` (окно выбора `WindowPickerWindow`) |
| `controlInput`* | `ControlPeerById` → `InputSharingService.ControlPeer` |
| `secondMonitor`* / `becomeSecondMonitor`* | `ToggleSecondMonitorAsync` / `BecomeSecondMonitorAsync` |
| `openOnDevice` | диалог выбора файлов → `PeerFilesService.OpenOnDeviceAsync` |
| `updateDevice`* | `PeerFilesService.SendOwnBuildAsync` |
| `soundHere` | `text` = `always` / `never` / иное (авто) → `AudioService.SetIncomingChoiceAsync` |
| `soundTo` | `text` = `here` → `KeepSoundHere()`; `auto` → `SendSoundTo(null)`; иначе `SendSoundTo(id)` |
| `microphone` | включить или выключить передачу микрофона этого устройства на `deviceId` → `MainViewModel.ToggleMicrophoneAsync`; ответ — строка с исходом (куда воспроизводится там или почему не началось) |
| `returnCursor`, `returnWindows` | как в трее |
| `addAddress` | `text` в `Settings.ManualPeerAddresses` + `SaveSettings()` + `MainViewModel.ReloadManualAddresses()` (иначе «Применить» в разделе «Сеть» записывало список, каким он был при запуске) |
| `removeAddress`* | удаляет адрес и сохраняет |
| `openSettings` | делегат `OpenSettings` = `App.ShowMainWindow` |
| `copyAddress`, `openShare`, `remoteDesktop`, `ping`, `latency`, `peerLog`, `reconnect`* | инструменты устройства (см. «Диагностика и инструменты») |
| `console`, `consoleElevated` | `MainViewModel.OpenConsoleOn(id, elevated)` |

**Ограничения и запасные пути.** Неизвестная команда → «Непонятная команда: …». Отказ в `RemoteGuard` → «Отменено.».

### Мост PanelUi (боковая панель)

**Что делает.** Снимок открытых просмотров, устройств, звука и ввода для боковой панели и её команды.

**Как устроено.** `Services/PanelUi.cs`. Методы: `panel.get` → снимок; `panel.command` c `{name, streamId, deviceId?, monitor?, text?, on?, screenX?, screenY?}`. Событие `panel.changed` — после каждой собственной команды, при `ButtonBarService.Changed`, и при новых миниатюрах мониторов (не чаще раза в 1000 мс и только пока панель показана). Таймер 1 с вызывает `IViewerControl.WantMonitorPictures(панель показана)`.
Фильтр удалённого нажатия: если `screenX/screenY` совпадают с последним нажатием смотрящего устройства (`RemoteInputTracker.IsRemote`: 12 px, 800 мс), команда не выполняется, панель прячется.

Снимок: `localActive`, `ownSound`, `inputFocus`, `stats`, `views[]` (`streamId`, `title`, `peerId`, `kind`, `active`, `inputAllowed`, `inputOn`, `zoom`), `bar` (`visible`, `active`, `presets[]`), `input` (`enabled`, `syncKeyboardLayout`, `adminControl`), `devices[]` (`id`, `name`, `screenOpen`, `secondMonitor`, `canBecome`, `sound` {`here`: `Automatic|Always|Never`, `playing`, `sentTo`}, `monitors[]` {`index`, `label`, `primary`, `key`, `image` (data-URL JPEG), `shown`}).

| name | Что делает |
|---|---|
| `showLocal` | `ShowLocalDesktop()` — сворачивает просмотры экранов |
| `switchView`, `closeView` | по `streamId` |
| `keyboard` | `ToggleKeyboard(streamId)` |
| `zoom` | `text` = `in` / `out` / иное (сброс) |
| `stats` | `MainViewModel.ScreenShowStats` = `on` или переключение |
| `mark` | `MarkHitch(streamId)` — отметка «подвисло» в журнале |
| `viewInput` | `SetViewInput(streamId, on)` |
| `showScreen` | `ShowOrSwitchScreen(deviceId, monitor, text=ключ монитора)`, панель прячется |
| `barVisible`, `barPreset` | `ButtonBarService.SetVisible` / `SetActive(text)` |
| `editButtons` | редактор кнопок, панель прячется |
| `inputEnabled`, `syncKeyboardLayout`, `adminControl` | одноимённые свойства `MainViewModel` |
| `openHub`, `openSettings`, `hideButton`, `close` | главное окно / вкладка «Параметры» / скрыть плавающую кнопку / спрятать панель |
| любое другое | `HubUi.RunCommandAsync(name, deviceId, null, text)` |

**Ограничения и запасные пути.** `inputEnabled` и `adminControl` из панели идут в модель без `RemoteGuard` (в `settings.set` те же переключатели переспрашиваются); защищает только фильтр по координатам нажатия.

### Мост SettingsUi (вкладка «Параметры»)

**Что делает.** Один снимок всех настроек, не относящихся к конкретному устройству, изменение значения по имени и кнопки-команды.

**Как устроено.** `Services/SettingsUi.cs`. Методы: `settings.get`; `settings.set` `{key, value, on?}`; `settings.command` `{name, streamId}` (возвращает `MainViewModel.StatusText`). Событие `settings.changed` — после каждого `set`/`command`, при `FeatureHealth.Changed` и при изменении `StatusText`, `AudioStatusText`, `ClipboardActivity`, `SmoothingText`.

Снимок: `device` {`name`, `id`, `fingerprint`, `network`, `version`, `versionText`, `status`}; `input` {`enabled`, `edgeResistance`, `smoothing`, `maxSmoothingDelay`, `syncKeyboardLayout`, `smoothingText`, `adminControl`, `adminControlText`, `adminHelper`, `adminNoPrompt`, `elevationText`, `canRestartElevated`}; `clipboard` {`enabled`, `shareFiles`, `dragDrop`, `maxInline`, `directPaste`, `activity`}; `audio` {`send`, `followInput`, `pinnedId`, `muteLocal`, `receive`, `bitrate`, `buffer`, `status`, `targets[]`}; `screen` {`allowViewing`, `allowControl`, `windowDrag`, `hideSentWindow`, `autoKeyboard`, `keyboardButton`, `directPresent`, `swipeMonitors`, `showStats`, `dictationButton`, `sharpStill`, `closeUnusedMinutes`, `maxFps`, `quality`, `codec`, `codecs[]`, `videoBitrate`, `virtualDisplay`, `virtualDisplayInstalled`, `sources[]`}; `shell` {`allowCommands`, `allowElevatedCommands`}; `power` {`saving`, `fps`, `savingNow`, `onBattery`, `options[]`}; `network` {`discoveryPort`, `transportPort`, `interfaces[]`, `addresses[]`}; `startup` {`withWindows`, `autoUpdate`}; `health[]` {`name`, `ok`, `detail`}.

Ключи `settings.set` — в таблице «Свойства MainViewModel и настройки» ниже. Переспрашиваются через `RemoteGuard`: `screen.allowControl`=false, `screen.allowViewing`=false, `input.enabled`=false, `input.adminControl`=true.

| Команда | Что делает |
|---|---|
| `installVirtualDisplay`* | `MainViewModel.InstallVirtualDisplayDriverAsync()` |
| `applyNetwork`* | `ApplyNetworkCommand`: имя, порты, интерфейсы, адреса → `SaveSettings` → `node.RestartNetworkAsync()` |
| `refreshInterfaces` | перечитать сетевые интерфейсы |
| `addFirewall`* | правила брандмауэра |
| `openLogs` | открыть папку журналов в Проводнике |
| `checkUpdates` | ручная проверка обновлений |
| `openRepository` | открыть `https://github.com/XKonnoRX/WinMirroring` |
| `restartElevated`* | перезапуск от администратора |
| `adminNoPrompt`* / `adminPrompt`* | задание Планировщика для помощника: создать / убрать |
| `stopSource`* | остановить исходящую трансляцию `streamId` |
| `returnHome` | `InputSharingService.ReturnHome()` (страница не вызывает) |

**Ограничения и запасные пути.** С 1.0.77 число вне допустимого диапазона становится ближайшим допустимым (обработчики `On…Changed` сами ставят свойству `Math.Clamp`), и страница показывает именно его; раньше оно молча не сохранялось, а страница продолжала показывать введённое. Порты и интерфейсы вступают в силу только по `applyNetwork`. Команды моста `addAddress` / `removeAddress` с 1.0.76 обновляют и `MainViewModel.ManualAddresses` (`ReloadManualAddresses`), так что «Применить» больше не откатывает адреса.

### Защита от нажатия с другого устройства (RemoteGuard)

**Что делает.** Действие, способное отрезать это устройство от того, кто им управляет удалённо, сначала переспрашивается окном на том мониторе, где указатель. Сидящего за устройством не спрашивают.

**Как устроено.** `Services/RemoteGuard.cs`: `IsRemote = RemoteInputTracker.ActiveWithin(4000 мс) || InputSharingService.IsBeingControlled`. `ConfirmAsync(what, why, yes)`: модальное окно «WinMirroring — подтверждение», кнопка по умолчанию «Отмена»; кнопка подтверждения активируется через `ArmAfter = 1 с`; без ответа через `GiveUpAfter = 30 с` — отмена. Вопрос и ответ пишутся в журнал.
Применяется: выход из трея; `hub.command` `controlInput`, `secondMonitor`, `becomeSecondMonitor`, `updateDevice`, `reconnect`, `microphone` (только включение), `removeAddress`; `settings.set` (четыре ключа выше); `settings.command` `applyNetwork`, `addFirewall`, `adminNoPrompt`, `adminPrompt`, `restartElevated`, `installVirtualDisplay`, `stopSource`.

**Ограничения и запасные пути.** Нет диспетчера — ответ «нет». Ключи `shell.allowCommands` / `shell.allowElevatedCommands` в списке защищаемых отсутствуют.

### Окна просмотра экрана (App.Viewers)

**Что делает.** На каждый входящий поток — своё окно `ScreenViewerWindow`, открытое на весь экран; на одно устройство — один просмотр экрана, запрос другого монитора переключает его.

**Как устроено.** `App.Viewers.cs`, `App` реализует `IViewerControl`. `ScreenStreamingService.ViewerOpened → OpenViewerWindow`: проверка `TakeOverFromWaiting`, создание окна с `WaitToResume`, `Show()` → `OpenFullScreenExternal()` → `BringToFront()`, `_activeView = StreamId`, `KeepFloatingOnTop`, `UpdateKeepAwake`, `StartUnusedViewWatch`. `Closed` убирает окно из `_openViewers` и ожидания.
Методы: `ShowOrSwitchScreen(peerId, index, key)` (есть просмотр — `SwitchToMonitor` и вперёд; нет — `RequestScreenAsync`), `OpenViews`, `IsLocalDesktopActive`, `ShowLocalDesktop()` (сворачивает всё, кроме копий окон `Kind == "window"`), `SwitchTo`, `CloseView`, `SwitchMonitor`, `ShownMonitorOf`, `ToggleKeyboard`, `SetViewInput`, `ZoomView`, `MarkHitch`, `MonitorPictures(peerId)` (JPEG → data-URL), `WantMonitorPictures(on)` (только при `MonitorCount > 1`), событие `MonitorPicturesChanged`.

**Ограничения и запасные пути.** `FindScreenView` не считает окна, ждущие возобновления.

### Повторное открытие просмотра после обрыва (App.Resume)

**Что делает.** Просмотр экрана, потерявший соединение, не закрывается: остаётся последний кадр, а когда устройство вернулось, тот же монитор запрашивается заново и новое окно незаметно заменяет старое (с тем же состоянием управления и свёрнутости).

**Как устроено.** `App.Resume.cs`. `WaitToResume(window)` вызывается окном, когда `ScreenViewer.CanResume` (`ViewEndings.CanResume`: обрыв соединения либо причина источника `shutdown`, `error`, `connection…`). Условия: не выход, `Kind == "monitor"`, не превышен предел `ViewEndings.AllowAnother` — `MostInARow = 3` за `InARowWithin = 60 с` на устройство. Одно ожидание на устройство (прежнее окно закрывается). Таймер `ResumeTick` — 500 мс:
- первый запрос через 700 мс после обрыва, далее каждые `AskAgainAfter = 3 с`, только при сессии в `SessionState.Ready`: `RequestScreenAsync(peerId, MonitorIndex, MonitorKey)`;
- окно закрывается после `MostAsks = 8` запросов без ответа или по истечении `ResumeWithin = 2 мин`; с 1.0.77 пауза между запросами удваивается после каждого второго (3, 3, 6, 6, 12, 12, 24, 24 с — `AskAgainAfterMs`), так что запросы растянуты на полторы минуты, а не кончаются за 24 с.
`TakeOverFromWaiting`: открывшийся просмотр забирает ожидание; если ожидания нет, но просмотр этого устройства переоткрыт менее 15 с назад и уже есть — это второй ответ на повторённый запрос, он закрывается без окна. `ForgetWaiting` — окно закрыли руками.

**Ограничения и запасные пути.** Копии окон (`Kind == "window"`) не возобновляются. При готовой сессии и молчащем источнике окно закроется примерно через 8 × 3 с, раньше двух минут.

### Неиспользуемые просмотры и запрет сна

**Что делает.** Свёрнутый просмотр экрана закрывается через заданное число минут; пока экран смотрят (здесь или отсюда), дисплей не гаснет.

**Как устроено.** `App.Viewers.cs`: `_unusedSince` отмечает момент сворачивания (`StateChanged`), таймер 30 с → `CloseUnusedViews`: только `Kind == "monitor"`, уведомление «Трансляция закрыта», `window.CloseView()`. `UpdateKeepAwake`: видимый несвёрнутый просмотр или `ScreenStreamingService.IsStreamingScreen` → `Services/KeepAwake.cs` `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)` на UI-потоке.

**Настройки.** `ScreenSettings.CloseUnusedViewMinutes` (5; 0 — не закрывать; ключ `screen.closeUnusedMinutes`, 0…600).

### Автообновление с GitHub

**Что делает.** Приложение само находит новый релиз, скачивает его с индикатором на экране и устанавливает, когда устройство не занято; затем перезапускается с прежними аргументами. Есть ручная проверка.

**Как устроено.** `App.StartUpdateChecks` / `RunUpdateFlowAsync` (`App.xaml.cs`), `Services/GithubUpdater.cs`, `scripts/update.ps1`.
- Расписание: при `AppSettings.AutoUpdate` — первая проверка через 8 с после запуска, далее `DispatcherTimer` 6 ч; после неудачи — 30 мин. Ручная: `settings.command checkUpdates` → `App.CheckForUpdatesNowAsync` (`manual: true`).
- Поиск: `GET https://api.github.com/repos/XKonnoRX/WinMirroring/releases/latest` — 3 попытки по 30 с, пауза 2·n с на статусах 408/425/429/500/502/503/504 и сетевых ошибках. `tag_name` (`vX.Y.Z`) → `Version` из трёх чисел; `<= CurrentVersion` (версия входной сборки) → «актуально». Нужен ассет `WinMirroring-win-x64.zip`. Если API не ответил — запасной путь: `https://github.com/XKonnoRX/WinMirroring/releases/latest` без следования перенаправлению (2 попытки, пауза 2 с), тег из `Location`, URL `…/releases/download/{tag}/WinMirroring-win-x64.zip`.
- Скачивание: `%TEMP%\WinMirroring-{tag}.zip`, 4 попытки, пауза 3·n с, прогресс не чаще 10 раз/с; общий предел потока 12 мин, таймаут `HttpClient` 10 мин; `User-Agent: WinMirroring-Updater`.
- Проверка содержимого: SHA-256 из `assets[].digest` API GitHub (с 1.0.77), подписи нет; `update.ps1` требует, чтобы в архиве был `WinMirroring.exe`.
- Занятость: `IsIdleForUpdate` = нет `ScreenStreamingService.HasActivity` и нет `InputSharingService.IsBeingControlled`. Автоматическая проверка на занятом устройстве оставляет архив в `_pendingUpdateZip` (в памяти) и ставит его на следующем тике таймера, если устройство свободно, либо при ручной проверке.
- Установка: `GithubUpdater.Apply(zip)` → `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<папка>\update.ps1" -Source "<zip>"` без окна. Скрипт: находит запущенный `WinMirroring.exe` (папка установки и аргументы), распаковывает архив в `%TEMP%\WinMirroring-update-<guid>`, взводит событие `Local\WinMirroring.App.Exit`, ждёт 20 с, иначе `Stop-Process -Force`; `robocopy /MIR /R:2 /W:1` до 3 раз (код ≥ 8 — ошибка); запускает `WinMirroring.exe <прежние аргументы> --restarted`. Параметры скрипта: `-Source`, `-Target`, `-Build`, `-NoStart`, `-DryRun`.
- На экране: уведомления «Скачиваю версию…», «Устанавливаю …», индикатор `FileTransferWindow.Report` («Скачиваю с GitHub», «Установится, когда устройство освободится», «Устанавливаю, приложение перезапустится»).
- Релиз собирает `.github/workflows/release.yml` по тегу `v*`: self-contained win-x64, архив `WinMirroring-win-x64.zip`.

**Настройки.** `AppSettings.AutoUpdate` (true; ключ `startup.autoUpdate`).

**Ограничения и запасные пути.** С 1.0.77: таймер проверок работает всегда, а `AppSettings.AutoUpdate` читается на каждом тике — переключатель действует сразу; пока скачанное обновление ждёт свободного устройства, таймер тикает раз в минуту (`PendingUpdateLooksEvery`) и ставит его, как только устройство освободилось (раньше — на следующей проверке через 6 ч, причём занятое устройство скачивало архив заново); результат `GithubUpdater.Apply` проверяется (`App.Install`): не запустился `update.ps1` — индикатор и уведомление говорят «не установлено»; архив сверяется с SHA-256, который GitHub отдаёт в `assets[].digest` (`AvailableUpdate.Sha256`; на запасном пути без API контрольной суммы нет), несовпадение — повторная загрузка; при запуске удаляются оставшиеся в `%TEMP%` `WinMirroring-*.zip` и `WinMirroring-update-*` старше суток (`GithubUpdater.CleanUpLeftovers`). Отложенный архив по-прежнему не переживает перезапуск — после запуска проверка и скачивание идут заново. Подписи кода нет. Скрипт отказывается ставить в папку с файлами без `WinMirroring.exe` и когда источник совпадает с установкой. **Обновление целиком (скачать → сверить → установить) после этих правок не прогонялось**: первым его проверит переход на следующую версию.

### Обновление соседнего устройства своей сборкой и «Открыть на устройстве»

**Что делает.** Устройство без доступа к GitHub обновляется с соседа: тот упаковывает собственную папку установки и передаёт её по существующему соединению. Тем же каналом файл отправляется на другое устройство, чтобы открыться там. Получатель всегда подтверждает.

**Как устроено.** `Services/PeerFilesService.cs`, `Services/DeviceInfoService.cs`.
- Версии: при готовности сессии стороны обмениваются `DeviceInfoMessage(AppVersion, IsReply)`; `PeerVersion(id)`, `CanUpdate(id)` = версия соседа известна и меньше локальной (флаг `canUpdate` в `hub.get`, строка `MainViewModel.VersionLineOf`).
- Отправка сборки (`hub.command updateDevice` → `SendOwnBuildAsync`): у соседа версия не ниже — «обновлять нечего»; `PackOwnBuild` архивирует `AppContext.BaseDirectory` рекурсивно (`CompressionLevel.Fastest`, нечитаемые файлы пропускаются) в `%TEMP%\WinMirroring-{версия}-{имя машины}.zip`; `ClipboardSyncService.SendFilesAsync(peer, [zip], SendFilesPurpose.Update, note = версия)`; архив удаляется после отправки.
- Приём (`ClipboardSyncService.FilesOffered` → `TakeUpdateAsync`): предложенная версия не выше своей — отказ «здесь уже X»; `MessageBox` «Обновление с другого устройства … Устанавливать?»; файлы принимаются в `%TEMP%\WinMirroring-update-<guid>`; первый `.zip` уходит в `GithubUpdater.Apply` (тот же `update.ps1`).
- «Открыть на устройстве» (`openOnDevice`): `OpenFileDialog` (несколько файлов) → `SendFilesAsync(..., SendFilesPurpose.Open)`. Приём (`TakeToOpenAsync`): `MessageBox` «Принять и открыть?» → `%LocalAppData%\WinMirroring\received\<yyyy-MM-dd HH-mm-ss>\` → `Process.Start` через оболочку для каждого пути → уведомление.

**Ограничения и запасные пути.** Не задан `Confirm` — ничего не принимается. Сосед, не сообщивший версию (старая сборка), кнопку обновления не получает. Не удалось запустить `update.ps1` — уведомление «Обновление не установлено».

### Журнал

**Что делает.** Все компоненты пишут в один файл на день в папке данных; папка открывается кнопкой.

**Как устроено.** `App.ConfigureLogging`: Serilog, `MinimumLevel.Debug`, файл `%LocalAppData%\WinMirroring\logs\app-<дата>.log`, `RollingInterval.Day`, `retainedFileCountLimit: 7`, шаблон `{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}`. Все `ILogger<T>` служб идут туда же (`SerilogLoggerFactory`). Из оболочки пишутся: версия при старте, сбои запуска функций, длительность каждого шага завершения, необработанные исключения UI и задач, сбои запросов интерфейса, решения `RemoteGuard`, ход проверки и скачивания обновления, вывод winget, закрытие и возобновление просмотров, включение запрета сна. `MainViewModel.OpenLogsCommand` открывает папку.

**Ограничения и запасные пути.** Предел размера файла в коде не задан (действует умолчание Serilog).

### Диагностика и инструменты устройства

**Что делает.** Состояние запуска каждой функции, журнал другого устройства без похода к нему, замер задержки и обычные инструменты Windows по адресу устройства.

**Как устроено.**
- Состояние функций: `FeatureHealth.Snapshot` → `settings.get.health[]` и `MainViewModel.FeatureStatuses` (старое окно).
- Журнал соседа (`Services/DiagnosticsService.cs`, команда `peerLog`): `LogRequestMessage(id)` по сессии, ожидание 20 с; отвечающая сторона шлёт последние `MaxLogBytes = 2 МБ` самого свежего `app-*.log`; файл сохраняется в `%LocalAppData%\WinMirroring\remote-logs\<имя>-<yyyyMMdd-HHmmss>.log` и выделяется в Проводнике. Отвечают только сессии в состоянии Ready.
- `latency` (`MeasureLatencyToAsync`): `LatencyProbeCount = 20` × `session.PingAsync(2 с)` с паузой 50 мс; среднее, мин, макс, разброс, потери.
- `ping`: `cmd.exe /k ping -t <host>`. `remoteDesktop`: `mstsc.exe /v:<host>`. `copyAddress`: адрес без порта в буфер обмена.
- `openShare` (`Services/NetworkShare.cs`): проверка TCP 445 (2,5 с) → `WNetAddConnection3W` к `\\host\IPC$` без запроса, при ошибке учётных данных — со стандартным диалогом Windows → список общих папок (`NetShareEnum`) → открытие `\\host`. Исходы: `Opened`, `Unreachable`, `Cancelled`, `Refused`, `Failed`; IPv6 записывается как `*.ipv6-literal.net`.
- `reconnect`: `session.CloseAsync("reconnect requested")`; сопряжённое устройство переподключается само.
- Живые числа: таймер `MainViewModel` 2 с, пока видимо любое окно приложения, кроме плавающей кнопки (иначе 6 с без работы): `RttText` устройств, `SmoothingText`, `AudioStatusText`.

**Настройки.** Нет (выдачу журнала отключить нельзя).

### Выполнение команд на другом устройстве

**Что делает.** Из карточки устройства открывается консоль на нём (обычная или администратора). Принимать такие консоли устройство начинает только после включения настройки, и первая консоль от каждого устройства подтверждается на месте.

**Как устроено.** Запуск функции «Выполнение команд» в `OnStartup`: `ShellService.Confirm` (окно Да/Нет), `ShellService.Notify` (уведомление «WinMirroring: …» об открытии и закрытии консоли), `ShellService.Start()`, `LocalShellServer.Start()` — именованный канал `WinMirroring-local-<SID пользователя>` (`src/WinMirroring.Shell/Local/LocalShellPipe.cs`), через который `wm-cli` на этой же машине исполняет команды на сопряжённых устройствах от имени приложения. Исходящая консоль: `hub.command console` / `consoleElevated` → `MainViewModel.OpenConsoleOn` → `ShellService.Open(peerId, elevated)` → `Views.RemoteConsoleWindow`.

**Настройки.** `ShellSettings.AllowCommands` (false; `MainViewModel.AllowRemoteCommands`, ключ `shell.allowCommands`), `ShellSettings.AllowElevatedCommands` (false; `AllowElevatedRemoteCommands`, ключ `shell.allowElevatedCommands`).

**Ограничения и запасные пути.** Нет готовой сессии — «Консоль на … не открыть: нет готового соединения.». Протокол и проверки на принимающей стороне — в `WinMirroring.Shell` (другой раздел).

### Уведомления и индикатор

**Что делает.** События показываются всплывающими уведомлениями значка в трее; длительные операции — маленьким окном с полосой.

**Как устроено.** `App.ShowNotification(title, message)` → `TaskbarIcon.ShowNotification`; сбой показа — Debug в журнал. Источники: разорванное с той стороны сопряжение; окно администратора не принимает ввод; нет нужной раскладки; сбои запуска функций; состояние помощника администратора; добавление разрешения в драйвер; второй монитор (включён / не получилось); закрытие неиспользуемого просмотра; обновление (проверка, скачивание, установка, ошибки); файлы и обновление от соседа; консоль (`ShellService.Notify`); ход сопряжения (`DialogPairingUi`); результаты «Вернуть курсор» / «Вернуть окна на место» из трея.
Индикатор — `Views.FileTransferWindow` (одно окно, исключено из захвата): `Update(FileTransferProgress)` для передачи файлов, `Report(...)` для обновления; `total = 0` — полоса без доли.
Сопряжение (`Services/DialogPairingUi.cs`): входящий запрос — модальное `PairingWindow` (закрывается при отмене токена); `PairingStarted` / `PairingFinished` → `StatusText` и уведомление; причины: `rejected`, `cancelled`, `timeout`, `not-pairable`, `connection-lost`.

### Экономия заряда

**Что делает.** Режим «тратить меньше»: никогда, на батарее или всегда; в нём частота кадров трансляции ограничена отдельным значением.

**Как устроено.** `MainViewModel.PowerSaving` (строка — имя значения `PowerSaving`) и `PowerFps` пишут `Settings.Power`. Состояние считает `PowerMode` (`src/WinMirroring.Core/Power/PowerMode.cs`): `Saving`, `OnBattery` (`GetSystemPowerStatus`, ответ хранится 5 с), `Fps(configured)`, `Every(usually, whileSaving)`. В снимке настроек: `power.savingNow`, `power.onBattery`, варианты «Никогда», «Когда устройство на батарее», «Всегда». В самой оболочке на расход влияют: усыпление скрытых страниц WebView2, таймер статистики 2 с / 6 с, закрытие неиспользуемых просмотров.

**Настройки.** `PowerSettings.Saving` (`OnBattery`; ключ `power.saving`), `PowerSettings.Fps` (30; ключ `power.fps`, 1…240).

### Драйвер виртуального монитора и другие установщики

**Что делает.** Кнопка в настройках ставит или обновляет Virtual Display Driver, нужный устройству, чей экран расширяется на другое; недостающее разрешение добавляется в драйвер автоматически с запросом прав.

**Как устроено.** `MainViewModel.InstallVirtualDisplayDriverAsync` (`ViewModels/MainViewModel.Screen.cs`): `winget.exe install|upgrade --id=VirtualDrivers.Virtual-Display-Driver -e --source winget --silent --accept-source-agreements --accept-package-agreements --disable-interactivity` (вывод в журнал; коды `0x8A15002B` и `0x8A150061` — «уже стоит»); затем `VirtualDisplaySetup.FindPackage()` и `InstallElevatedAsync(package)` (повышенный запуск этого же exe с `--vdd-install`; код −1 — UAC отклонён); до 30 × 500 мс ожидание `ScreenStreamingService.IsVirtualDisplayAvailable`; `DetachLeftoverDisplay()`. Одновременно идёт только одна установка. Добавление разрешения: `screen.AddDisplayModeAsync` → `VirtualDisplaySetup.AddModeElevatedAsync(w, h)` (запуск с `--vdd-add-mode`). Свойства `VirtualDisplayStatus`, `VirtualDisplayInstalled`.
Другие внешние процессы, запускаемые оболочкой: `powershell.exe` (update.ps1), `cmd.exe /c netsh` (брандмауэр), `mstsc.exe`, `cmd.exe /k ping`, `explorer.exe /select`, `osk.exe` / `TabTip.exe`, сам exe с `runas`.

**Ограничения и запасные пути.** Нет winget — открывается страница `https://github.com/VirtualDrivers/Virtual-Display-Driver/releases`. Драйвер установлен, но не виден — совет перезагрузить устройство.

### Перетаскивание файлов между устройствами (DragDropCoordinator)

**Что делает.** Файл, который тащат мышью, переходит через край экрана на другое устройство вместе с курсором; файлы можно бросать в окно просмотра и вытаскивать из него. Содержимое скачивается только при отпускании.

**Как устроено.** `Services/DragDropCoordinator.cs` связывает `InputSharingService` и `ClipboardSyncService.Drag`. Управляющая сторона: `FocusChanged` при зажатой левой кнопке → `CatchLocalDragAsync` (`Drag.CatchAsync`, `CatchTimeout = 300 мс`) → `SendDragAsync(start: true)`. Управляемая: `OnDragStart` (только от текущего контроллера) → `InjectLocalButton` + `Drag.StartDragAsync`; уход курсора обратно — `BeforeEdgeHandOff = HandOffAsync` (сначала `screen.TrySendMovingWindowAsync` — окно, которое тащат за заголовок, уходит потоком окна). `ControlEnding` отменяет активный перенос.
`DragDropCoordinator.Viewer.cs`: `DropIntoViewerAsync` (сообщение `ViewerDropMessage`; источник переводит точку `TryMapToScreen`, сам нажимает и отпускает кнопку), `CatchFromViewerAsync` (`ViewerCatchMessage`, ответ ждётся `ViewerReplyTimeout = 3 с`, захват `ViewerCatchTimeout = 400 мс`), `StartViewerDragAsync`.

**Настройки.** `ClipboardSettings.DragDropEnabled` (true; ключ `clipboard.dragDrop`).

**Ограничения и запасные пути.** Нет OLE-поверхности (`clipboard.Drag is null`) — файловая часть выключена, перенос окон остаётся. Нет соединения с владельцем файлов — перенос игнорируется с записью в журнал.

### Экранная клавиатура и исключение из захвата

**Что делает.** Касание текстового поля в трансляции поднимает клавиатуру у смотрящего; собственные оверлеи приложения не видны тому, кто смотрит этот экран.

**Как устроено.**
- `Services/RemoteKeyboardWatcher.cs` (сторона источника): по `RemoteInputTracker.TouchEnded` через `Settle = 250 мс` смотрит элемент в фокусе через UI Automation (Edit / Document / ComboBox, не только для чтения) и шлёт `ScreenStreamingService.SendKeyboardHintAsync(peer, stream, editable)`; «убрать» шлётся только после «показать».
- `Services/TouchKeyboard.cs`: `Toggle()` — COM `ITipInvocation` (при необходимости запускает `TabTip.exe`), запасной путь `osk.exe`.
- `Services/InputPaneWatcher.cs`: положение сенсорной клавиатуры через `IFrameworkInputPane` (события `Showing` / `Hiding`, свойство `Location`); использует окно просмотра.
- `Services/CaptureExclusion.cs`: `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE = 0x11)` для плавающей кнопки, панели и индикатора.

**Настройки.** `ScreenSettings.AutoKeyboardOnTap` (false), `KeyboardButton` (true).

### Хранение настроек и состояния

**Что делает.** Настройки и служебные данные лежат в профиле пользователя и сохраняются сразу при изменении.

**Как устроено.** `SettingsStore` (`src/WinMirroring.Core/Settings/SettingsStore.cs`): `%LocalAppData%\WinMirroring\settings.json` (camelCase, запись через `.tmp` и замену; нечитаемый файл копируется в `settings.json.corrupt`, берутся умолчания). В том же каталоге: `logs\`, `remote-logs\`, `received\`, `webview\`. Вне его: `HKCU\…\Run` (автозапуск), `%TEMP%` (архивы обновлений). Из состояния окон оболочка хранит только положение и скрытость плавающей кнопки.

### Свойства MainViewModel и настройки

`ViewModels/MainViewModel*.cs`; свойство пишет настройку в частичном методе `On…Changed` и сразу вызывает `node.SaveSettings()` (кроме сетевых — они по «Применить»).

| Свойство модели | Ключ `settings.set` | Настройка (умолчание) | Проверка / побочное действие |
|---|---|---|---|
| `DeviceName` | `device.name` | `DeviceName` | `MirroringNode.Rename`; пусто → имя машины |
| `DiscoveryPort`, `TransportPort` | `network.discoveryPort`, `network.transportPort` | 47620, 47621 | 1…65535, по `applyNetwork` |
| `Interfaces[].IsSelected` | `network.interface` (id, `on`) | `SelectedInterfaceIds` (пусто = все) | по `applyNetwork` |
| `ManualAddresses` | — (старое окно) | `ManualPeerAddresses` | по `applyNetwork` |
| `StartWithWindows` | `startup.withWindows` | `StartWithWindows` (false) | реестр Run |
| `AutoUpdate` | `startup.autoUpdate` | `AutoUpdate` (true) | — |
| `InputEnabled` | `input.enabled` | `Input.Enabled` (true) | `InputSharingService.SetEnabled` |
| `EdgeResistance` | `input.edgeResistance` | `Input.EdgeResistance` (8) | 1…500 |
| `SmoothingEnabled` | `input.smoothing` | `Input.SmoothingEnabled` (true) | `ApplyInputSettings` |
| `MaxSmoothingDelay` | `input.maxSmoothingDelay` | `Input.MaxSmoothingDelayMs` (30) | 0…200 |
| `SyncKeyboardLayout` | `input.syncKeyboardLayout` | `Input.SyncKeyboardLayout` (true) | `SetSyncKeyboardLayout` |
| `AdminWindowControl` | `input.adminControl` | `Input.ElevatedInjector` (false) | `App.SetAdminWindowControlAsync` |
| `ClipboardEnabled`, `ClipboardShareFiles`, `ClipboardDragDrop` | `clipboard.enabled`, `.shareFiles`, `.dragDrop` | `Clipboard.Enabled`, `ShareFiles`, `DragDropEnabled` (true) | — |
| `ClipboardMaxInline` | `clipboard.maxInline` | `Clipboard.MaxInlineMegabytes` (64) | 1…1024 |
| `ClipboardDirectPaste` | `clipboard.directPaste` | `Clipboard.MaxDirectPasteMegabytes` (64) | 0…4096 |
| `AudioSendEnabled`, `AudioFollowInput`, `AudioMuteLocal`, `AudioReceiveEnabled` | `audio.send`, `.followInput`, `.muteLocal`, `.receive` | `Audio.SendEnabled`, `FollowInput`, `MuteLocalWhileSending`, `ReceiveEnabled` (true) | `AudioService.ApplySettings` |
| `AudioPinnedTarget` | `audio.pinnedId` | `Audio.PinnedTargetId` ("") | своё устройство и пустой выбор → "" |
| `AudioBitrate` | `audio.bitrate` | `Audio.BitrateKbps` (160) | 32…510 |
| `AudioBuffer` | `audio.buffer` | `Audio.BufferMs` (40) | 10…500 |
| `ScreenAllowViewing`, `ScreenAllowControl` | `screen.allowViewing`, `.allowControl` | `Screen.AllowViewing`, `AllowControl` (true) | — |
| `ScreenWindowDrag`, `ScreenHideSentWindow` | `screen.windowDrag`, `.hideSentWindow` | `WindowDragEnabled`, `HideSentWindow` (true) | — |
| `ScreenAutoKeyboard`, `ScreenKeyboardButton`, `ScreenDictationButton` | `screen.autoKeyboard`, `.keyboardButton`, `.dictationButton` | `AutoKeyboardOnTap` (false), `KeyboardButton` (true), `DictationButton` (true) | — |
| `ScreenDirectPresent`, `ScreenSwipeMonitors`, `ScreenSharpStill` | `screen.directPresent`, `.swipeMonitors`, `.sharpStill` | одноимённые (true) | — |
| `ScreenShowStats` | `screen.showStats`; команда панели `stats` | `Screen.ShowStats` (false) | — |
| `ScreenMaxFps` | `screen.maxFps` | `Screen.MaxFps` (60) | 1…60 |
| `ScreenQuality` | `screen.quality` | `Screen.Quality` (70) | 30…95 |
| `ScreenVideoCodec` | `screen.codec` | `Screen.VideoCodec` (`Auto`; `Jpeg`, `H264`, `Hevc`) | — |
| `ScreenVideoBitrate` | `screen.videoBitrate` | `Screen.VideoBitrateKbps` (12000) | 1000…100000 |
| — (напрямую в `SettingsUi`) | `screen.closeUnusedMinutes` | `Screen.CloseUnusedViewMinutes` (5) | 0…600 |
| `AllowRemoteCommands`, `AllowElevatedRemoteCommands` | `shell.allowCommands`, `.allowElevatedCommands` | `Shell.AllowCommands`, `AllowElevatedCommands` (false) | — |
| `PowerSaving`, `PowerFps` | `power.saving`, `power.fps` | `Power.Saving` (`OnBattery`), `Power.Fps` (30) | имя значения перечисления; 1…240 |

Через мост не меняются: `PinConnectionsToInterface`, `FindPeersOnTailscale`, `Audio.FollowScreenViewer`, `Screen.KeyboardButtonX/Y`, `MenuButton*`, `ButtonBar` (у последних свои пути записи).

### Старое окно: сетка раскладки и команды модели

**Что делает.** Запасной интерфейс на WPF (вкладки), когда React-страниц нет: список устройств с контекстным меню, сетка раскладки 4 × 3, сеть, настройки.

**Как устроено.** `MainViewModel`: коллекции `Peers` (`PeerItemViewModel`: имя, состояние по-русски, адрес, `Host`, `IsPaired`, `IsReady`, `RttText`, `LastError`), `Interfaces` (`InterfaceItemViewModel`, пометка «VPN/туннель»), `LayoutCells` (`GridColumns = 4`, `GridRows = 3`), `LayoutChoices` / `DeviceChoices` (`DeviceChoice`: устройство целиком или один его монитор). Команды: `PairCommand`, `UnpairCommand`, `SaveLayoutCommand` (запрещает одно и то же в двух ячейках → `InputSharingService.ApplyLayout`), `ReturnHomeCommand`, `ControlPeerCommand`, `ShowScreenCommand`, `SendWindowCommand`, `ToggleSecondMonitorForSelectedCommand`, `BecomeSecondMonitorOfSelectedCommand`, `StopScreenSourceCommand`, `MeasureLatencyCommand`, `IcmpPingCommand`, `RemoteDesktopCommand`, `OpenShareCommand`, `CopyAddressCommand`, `ReconnectCommand`, `ToggleListenCommand`, `TogglePinnedAudioCommand`, `FetchPeerLogCommand`, `ApplyNetworkCommand`, `RefreshInterfacesCommand`, `AddManualAddressCommand`, `RemoveManualAddressCommand`, `AddFirewallRulesCommand`, `OpenLogsCommand`, `CheckForUpdatesCommand`, `OpenRepositoryCommand`, `RestartElevatedCommand`. Те же методы с идентификатором устройства вызывает `HubUi`. `SendWindowToAsync`: после отправки окна ввод уходит на то же устройство (`ControlPeer`), если общий ввод включён и устройство не является вторым монитором. `InputFocusText` — «Ввод: здесь» / «Ввод: на … (Ctrl+Alt+Home вернёт)».

**Ограничения и запасные пути.** `MainWindow` при закрытии прячется; настоящее закрытие — только через `AllowClose()` при выходе.
