# Общий ввод (WinMirroring.Input)

> Часть справочника [docs/FEATURES.md](../FEATURES.md). Составлено по коду версии 1.0.75; источник правды — код.

Проекты: `src/WinMirroring.Input` (библиотека), `src/WinMirroring.InputHost` (отдельный exe-помощник с правами администратора). Настройки: `src/WinMirroring.Core/Settings/InputSettings.cs` (`AppSettings.Input`). Управляющие сообщения: `src/WinMirroring.Core/Messages/ControlMessage.cs`. Бинарный канал: `ChannelId.Input = 1`. Тесты: `tests/WinMirroring.Input.Tests`.

Термины: **управляющий** (controller) — устройство с физической мышью/клавиатурой; **управляемый** (controlled) — устройство, где ввод воспроизводится. Каждое устройство одновременно может быть и тем, и другим: всё это один класс `InputSharingService` (partial: `InputSharingService.cs`, `.Controller.cs`, `.Controlled.cs`, `.KeyboardLayout.cs`).

### Сервис общего ввода: роли и жизненный цикл

**Что делает.** Одна мышь и клавиатура на несколько устройств «как Mouse Without Borders»: курсор упирается в край экрана и переходит к соседу из раскладки, клавиатура идёт за курсором. Управляемое устройство сообщает, когда курсор выталкивают через его край, и управление идёт дальше или возвращается.

**Как устроено.** `InputSharingService` (`src/WinMirroring.Input/InputSharingService.cs`) создаётся с `MirroringNode`. `Start()` подписывается на `SessionReady`/`SessionClosed`, на `MonitorCatalog.Changed`, запускает воркер команд (`Channel<Command>`, один читатель), сторож раскладки клавиатуры и, если `Input.Enabled`, хуки. На каждой готовой сессии регистрируется обработчик `ChannelId.Input` и подписка на `ControlMessageReceived`; сразу уходят `LayoutMessage` (если `LayoutVersion > 0`) и `InputMonitorsMessage(isReply: false)`. Воркер — конечный автомат захвата; он никогда не ждёт сеть. Команды: `CrossEdgeCommand`, `EdgeReachedCommand`, `ReturnHomeCommand`, `ReleaseRequestedCommand`, `ControlPeerCommand`, `SessionLostCommand`, `CaptureLostCommand`. Исключение в обработчике команды завершает захват с причиной «internal error». Состояние наружу: `IsEnabled`, `IsCapturing`, `RemoteTargetId`, `IsBeingControlled`, `ControllerId`, `PlayoutDelayMilliseconds`, `Layout`; события `FocusChanged(peerId|null)`, `ControlledByChanged(peerId|null)`, `LayoutChanged`, `MonitorsChanged`, `RemoteInputBlocked`, `KeyboardLayoutMissing`, `ControlEnding`. `DisposeAsync` идемпотентен: завершает захват с `Leave`, снимает хуки, отпускает всё зажатое.

**Настройки.** `Input.Enabled` (по умолчанию `true`) — ставить ли хуки. `SetEnabled(bool)` сохраняет и ставит/снимает хуки (при снятии сначала `ReturnHomeCommand`).

**Ограничения и запасные пути.** `Enabled = false` выключает только роль управляющего (нет хуков → нет переходов и `ControlPeer` пишет в журнал «Input sharing is off»). Роль управляемого от этой настройки не зависит: обработчик канала Input регистрируется всегда, отдельного запрета «не принимать ввод» в `InputSettings` нет. Сессия считается пригодной целью, только если `State == Ready` и от пира что-то приходило за последние `UsableSilenceMs = 8000` мс.

**Тесты.** Сервис целиком покрыт только `LayoutRelayIntegrationTests` (три реальных узла, пересылка раскладки). Захват/хуки/воспроизведение юнит-тестов не имеют.

### Низкоуровневые хуки и что захватывается

**Что делает.** Пока ввод «дома», хуки только наблюдают. Пока ввод отдан другому устройству, локальные мышь и клавиатура проглатываются и уходят соседу, а локальный курсор скрыт.

**Как устроено.** `InputHookThread` (`src/WinMirroring.Input/Hooks/InputHookThread.cs`) — выделенный фоновый поток `WinMirroring.InputHooks` с циклом сообщений. На нём: `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`, message-only окно, зарегистрированное на Raw Input мыши (`RIDEV_INPUTSINK`), окно-«щит» курсора, хуки `WH_MOUSE_LL` и `WH_KEYBOARD_LL`. Три колбэка: `MouseFilter`/`KeyboardFilter` (вернуть `true` = проглотить) и `RawMouseMoved(dx, dy)`. События — `MouseHookEvent`/`KeyboardHookEvent` (`Hooks/HookEvents.cs`) с флагами `IsInjected` (LLMHF/LLKHF_INJECTED) и `IsOurInjection` (`dwExtraInfo == InjectionMarker`, `0x574D4952` «WMIR»). Исключение в фильтре логируется, событие пропускается дальше. Ошибка установки → `Start()` бросает `InvalidOperationException`.

Фильтр мыши (`OnMouseHook`, `.Controller.cs`): свои инъекции пропускает; физические кнопки (не injected) учитывает в `_physicalDownButtons`; без захвата ничего не трогает. При захвате: `WM_MOUSEMOVE` — чужие инъекции пропускает не пересылая; иначе смещение = `pt` хука минус текущая позиция (припаркованного) курсора, т. е. уже со скоростью и ускорением указателя этого устройства; шлёт `PostMove(MonotonicClock.NowWire(), dx, dy)` и проглатывает. Колесо (`WM_MOUSEWHEEL`/`WM_MOUSEHWHEEL`, знаковое старшее слово) и кнопки Left/Right/Middle/X1/X2 — пересылает и проглатывает. Фильтр клавиатуры (`OnKeyboardHook`): ведёт `_physicalDownKeys`, при захвате пересылает `vk`, скан-код, `up`, `extended` и проглатывает.

Raw Input (`HandleRawInput`): только `RIM_TYPEMOUSE`, только относительные данные (`MOUSE_MOVE_ABSOLUTE` — перо, касание, RDP — отбрасываются); буфер до 1024 байт. Используется исключительно для обнаружения упора в край.

Щит курсора: окно `WS_POPUP` 64×64 (`ShieldSize`), `LAYERED|TOPMOST|TOOLWINDOW|NOACTIVATE`, альфа 1/255, класс без курсора, на `WM_SETCURSOR` ставит пустой курсор. `ShowCursorShield(point)` ставит окно под точку и делает `SetCursorPos`; собственное перемещение хук узнаёт по координатам и окну 100 мс и не трогает. Точка парковки — `VirtualScreen.PrimaryCenter` (центр основного монитора), доступна как `CursorParkPoint`.

**Ограничения и запасные пути.** Если класс/окно щита не создались — предупреждение в журнал, локальный курсор остаётся видимым. Клавиатурные события, инжектированные чужими программами, при захвате пересылаются и учитываются в `_physicalDownKeys` (проверки `IsInjected` для клавиатуры нет, в отличие от движения мыши). `Dispose` ждёт поток 3 с.

**Тесты.** Нет.

### Переход курсора через край (управляющий)

**Что делает.** Нужно «надавить» на край монитора, за которым нет другого монитора этого устройства: накопив заданное усилие, курсор появляется на соседнем устройстве в соответствующей точке.

**Как устроено.** `OnRawMouse` (`InputSharingService.Controller.cs`) работает, только когда нет захвата и это устройство никем не управляется, раскладка не пуста и содержит это устройство. Берёт позицию курсора, монитор под ним (`MonitorCatalog.Current.Nearest`) и копит четыре счётчика `_pushLeft/Right/Top/Bottom`: прибавляет сырые отсчёты мыши, пока курсор стоит на крайнем пикселе монитора, движение направлено наружу и `MonitorSet.ContinuesBeyond` говорит, что за краем нет монитора с экраном; иначе счётчик обнуляется. При достижении `EdgeResistance` — `CrossEdgeCommand(edge, relative вдоль края монитора, relative вдоль края всего рабочего стола, ключ монитора)`. Воркер спрашивает `DeviceLayout.Across(...)`; если сосед — другое устройство, `IsCrossingSuppressed` не возражает и сессия пригодна — `BeginCapture`. Если курсор оказался на скрытом виртуальном мониторе (`IsOutOfSight`), его возвращают на ближайший видимый (`SnapToScreen` + `SetCursorPos`), а надавливание продолжает считаться.

**Настройки.** `Input.EdgeResistance` = 8 (сырые отсчёты мыши; не меньше 1; модель окна принимает 1…500).

**Ограничения и запасные пути.** Края считаются по каждому монитору, а не по объединённому прямоугольнику. Перо/касание переход не вызывают. `IsCrossingSuppressed` (ставит приложение: `screen.IsExtendedTo`) отключает переход на устройство, которое сейчас служит вторым монитором.

**Тесты.** `MonitorLayoutTests` (`AnEdgeIsOpenOnlyWhereNoOtherMonitorContinues`, `ThePointerIsNotLeftOnAMonitorNobodyCanSee`, `TheVirtualMonitorIsAPlaceAgainWhileSomebodyIsShowingIt`, `PointsOffEveryMonitorSnapOntoTheNearestOne`); `LayoutAndEdgeTests` (геометрия `EdgeMath`).

### Раскладка устройств: свободное поле и сетка

**Что делает.** Пользователь расставляет устройства так, как стоят их экраны. В свободной расстановке курсор переходит только там, где край монитора одного устройства касается края монитора другого, и выходит на той же высоте (или в той же горизонтальной позиции). Старая сетка: целый край ведёт к целому краю соседней ячейки, пропорционально.

**Как устроено.** `DeviceLayout` (`src/WinMirroring.Input/Layout/DeviceLayout.cs`), неизменяемый; строится `DeviceLayout.From(cells, placements, monitorsOf)`. Данные (в `InputSettings.cs`): `LayoutCell(DeviceId, Column, Row, Monitor?)` — ячейка сетки, `Monitor = null` значит «устройство целиком»; `LayoutPlacement(DeviceId, X, Y, Monitors?)` — где на общем поле лежит начало рабочего стола устройства (левый верх основного монитора), в пикселях; `LayoutRect(Key, Left, Top, Width, Height)` — монитор в координатах своего рабочего стола. Если есть хоть одна расстановка — `IsFree`, сетка игнорируется. Мониторы устройства берутся: живые (`monitorsOf`) → запомненные в `LayoutPlacement.Monitors` → один условный `DefaultScreen` 1920×1080.

`Across(deviceId, monitor, edge, relativeOnMonitor, relativeOnScreen)` → `Crossing(DeviceId, Monitor?, Entry, Relative)` или `null`. Свободное поле: берётся прямоугольник названного монитора (или объединение мониторов устройства, если монитор не назван — тогда используется `relativeOnScreen`), точка вдоль края, затем `CrossFrom` ищет монитор другого устройства, чей противоположный край отстоит не дальше `EdgeTolerance = 48` px и покрывает эту точку; при нескольких — с наименьшим зазором. Сетка: `CellOf` (ячейка монитора → ячейка «всё устройство» → первая ячейка устройства для запроса без монитора) и `Neighbor(cell, edge)`. Прочее: `Neighbor(deviceId, edge)`, `DirectionOf` (сторона, с которой стоит сосед; иначе преобладающее направление между центрами) — через `InputSharingService.SideOf` даёт "right"/"left"/"top"/"bottom" для размещения виртуального монитора; `ToCells()` выводит приближённую сетку из свободного поля для старых устройств; `HasMonitorCells`, `TryGetCell`, `Contains`, `IsEmpty`. `EdgeMath` (`Layout/Edge.cs`): `Opposite`, `RelativeAlong` (0…1, с зажимом), `PointOn(rect, edge, relative, inset)`.

Редактор: `ApplyPlacements(placements)` (новое окно, `HubUi` команда `hub.save` с `Placements`) дополняет каждую расстановку мониторами (живые → присланные → запомненные), выводит сетку `ToCells()` и сохраняет обе; `ApplyLayout(cells)` (старое окно и `hub.save` без расстановок) сохраняет сетку и очищает `Placements`. Данные редактору отдаёт `HubUi.Snapshot` (`src/WinMirroring.App/Services/HubUi.cs`): `cells`, `placements` (только `DeviceId, X, Y`) и для каждого устройства список мониторов (`Key`, подпись, размер, `Left/Top`, `Primary`); для неподключённого устройства мониторы берутся из `KeptMonitors`.

**Настройки.** `Input.Layout` (`List<LayoutCell>`, пусто), `Input.Placements` (`List<LayoutPlacement>`, пусто), `Input.LayoutVersion` (0), `Input.LayoutOrigin` ("").

**Ограничения и запасные пути.** Дубликаты (устройство/монитор дважды, две вещи в одной ячейке, устройство дважды в расстановках) молча игнорируются. `ToCells` — приближение: сетка не умеет «только нижняя половина края».

**Тесты.** `FreeLayoutTests`, `LayoutAndEdgeTests`, `MonitorLayoutTests` (`EachMonitorHasItsOwnNeighbours`, `CrossingsComeBackOntoTheMonitorTheyLeft`, `AMonitorWithoutACellFallsBackToTheWholeDeviceOrHasNoCrossing`, `DeviceLevelQueriesSeeEveryCellOfTheDevice`, `TheSameMonitorTwiceOrTwoThingsInACellAreIgnored`).

### Синхронизация раскладки между устройствами

**Что делает.** Сохранённая раскладка расходится по всем сопряжённым устройствам, в том числе тем, с кем автор не соединён напрямую; везде побеждает самая новая.

**Как устроено.** `Store` присваивает `LayoutVersion = LayoutVersion.Next(current, nowUnixMs)` = `max(now, current + 1)`, `LayoutOrigin` = id этого устройства, сохраняет настройки, шлёт всем готовым сессиям. Приём (`OnControlMessage`): `LayoutVersion.Compare` (версия, при равенстве — порядковое сравнение `Origin`; отсутствующий origin — наименьший). Чужая старее → отвечаем своей (если есть); равна → ничего; новее → принимаем `Cells`/`Placements`, сохраняем, `LayoutChanged`, пересылаем всем остальным сессиям. `LayoutMessage` входит в «вступительные заявления» `PeerSession` (удерживаются до 30 с и переигрываются обработчику, подключившемуся позже).

**Сообщения.** `LayoutMessage(Version, Cells, Origin?, Placements?)` — тип `"layout"`.

**Ограничения и запасные пути.** Старое устройство не знает `Placements` и `Origin` и живёт по `Cells`.

**Тесты.** `LayoutVersionTests`, `LayoutRelayIntegrationTests.ALayoutReachesADeviceItsAuthorIsNotConnectedTo`.

### Мониторы: каталог, геометрия, обмен списками (PeerMonitors)

**Что делает.** Каждое устройство знает свои мониторы и мониторы подключённых соседей — для рисования поля, для переходов на конкретный монитор и для того, чтобы курсор не терялся на невидимом виртуальном мониторе.

**Как устроено.** `MonitorCatalog` (`src/WinMirroring.Input/Screen/MonitorCatalog.cs`, статический): `Current` перечитывает `EnumDisplayMonitors` не чаще раза в `MaxAgeMs = 1000` мс; `Refresh()` — немедленно; `Changed` — когда изменилась подпись набора. `MonitorInfo(Key, Bounds, Primary, IsVirtual)`: ключ — имя дисплея `\\.\DISPLAYn`; виртуальным считается адаптер, у которого `DeviceString` содержит «Virtual Display» или `DeviceID` содержит «MttVDD». `MonitorSet`: `All`; `ForLayout` (без виртуального, по номеру дисплея); `Screens` (мониторы, куда можно пускать курсор: без виртуального, пока `MonitorCatalog.VirtualMonitorIsHidden()` истинно); `At`, `Find`, `Nearest`, `Snap`, `ContinuesBeyond`, `IsOutOfSight`, `SnapToScreen`, `Describe` («Монитор 2 · 1920×1080 · справа», номер как в Windows), `DisplayNumber`, `Signature`. `VirtualMonitorIsHidden` по умолчанию `() => true`; приложение ставит `() => screen.ExtendedTo is null` (`App.xaml.cs`). `VirtualScreen` (`Screen/VirtualScreen.cs`): прямоугольник всех мониторов, `TryGetCursor`, `SetCursor`, `PrimaryCenter`.

`InputSharingService.LocalMonitors` → `List<LayoutMonitor>` из `ForLayout`; `PeerMonitors(peerId)` — что прислал сосед (пусто для старого/неподключённого); `MonitorRects(deviceId)`; `KeptMonitors(deviceId)`; `RefreshMonitors()` (приложение зовёт на `SystemEvents.DisplaySettingsChanged`). При смене своих мониторов — рассылка `InputMonitorsMessage(isReply: true)`, пересборка `_layout`, `MonitorsChanged`. При закрытии сессии запись соседа удаляется. Наличие записи в `_peerMonitors` одновременно служит признаком возможностей: только таким устройствам шлются `EnterOnMonitor`/`EdgeReachedOnMonitor`.

**Сообщения.** `InputMonitorsMessage(Monitors, IsReply)` — `"input-monitors"`; на запрос (`IsReply = false`) отвечают своим списком. `LayoutMonitor(Key, Label, Primary, Width, Height, Left, Top)`.

**Ограничения и запасные пути.** Если мониторов не нашлось, `MonitorSet` подставляет один безымянный на весь виртуальный экран.

**Тесты.** `MonitorLayoutTests` (в т. ч. `MonitorsAreNumberedPrimaryFirstAndTheVirtualOneIsLeftOut`); `TheCatalogSeesThisMachinesMonitors` — `[Fact(Explicit = true)]`.

### Захват: начало, передача дальше, завершение, «управлять устройством»

**Что делает.** С момента перехода локальный ввод принадлежит другому устройству; его можно передать третьему, не возвращаясь домой, либо отдать вручную кнопкой «управлять» (курсор появится в центре экрана того устройства).

**Как устроено.** `Capture(session, sender)` хранится в `_capture` (lock-free чтение из потока хуков). `BeginCapture`: создаёт `InputSender`, ставит в очередь `Enter` (в центр / на монитор / обычный), запоминает клавиши и кнопки, зажатые до захвата (`_passThroughKeyUps`, `_passThroughButtonUps`), показывает щит, шлёт раскладку клавиатуры, `FocusChanged(peerId)`. `SwitchCapture`: новый `Enter` следующему устройству, атомарная замена `_capture`, старому — `Leave`. `EndCapture(capture, sendLeave, placement, reason)`: `Leave` (или `Abort`, если канал неисправен), снятие щита, при возврате через край — `SetCursorPos` в точку `EdgeMath.PointOn(монитор или весь экран, edge, relative, inset: 2)` с привязкой к монитору, сброс счётчиков, применение раскладки клавиатуры, `FocusChanged(null)`. `ControlPeer(peerId)` → `BeginCapture/SwitchCapture(atCenter: true)`.

Обратный путь: управляемый шлёт `EdgeReached*` → `EdgeReachedCommand` → `OnEdgeReached`: `Across` от имени управляемого устройства; если следующий — это устройство, `EndCapture` с размещением курсора; если третье — `SwitchCapture`.

**Ограничения и запасные пути.** Причины завершения в журнале: «returned by the user», «returned from the controlled device», «cursor came back», «session closed», «the other device stopped responding», «send queue overflow», «internal error», «shutdown».

**Тесты.** Нет.

### Канал Input: проводной протокол и отправка

**Что делает.** Компактный бинарный поток событий ввода; сетевые задержки никогда не блокируют локальную мышь и клавиатуру.

**Как устроено.** `InputCodec` (`src/WinMirroring.Input/Protocol/InputCodec.cs`), little-endian; кадр — одно или несколько событий подряд без префикса длины, каждое начинается с байта вида (`InputEventKind`):

| Вид | Байт | Тело |
|---|---|---|
| `MouseMove` 1 | 9 | `u32` метка времени (мкс, монотонные часы управляющего, с переполнением), `i16 dx`, `i16 dy` (зажим до int16) |
| `MouseButton` 2 | 3 | кнопка (`MouseButton`: 1 Left, 2 Right, 3 Middle, 4 X1, 5 X2), `down` |
| `MouseWheel` 3 | 4 | `i16 delta`, `horizontal` |
| `Key` 4 | 6 | `u16 vk`, `u16 scan`, флаги (`0x01` up, `0x02` extended) |
| `Enter` 5 | 6 | край (0 Left, 1 Right, 2 Top, 3 Bottom; `0xFF` — в центр), `f32 relative` |
| `Leave` 6 | 1 | — |
| `EdgeReached` 7 | 6 | край, `f32 relative` (вдоль края всего рабочего стола) |
| `Alive` 8 | 1 | — |
| `EnterOnMonitor` 9 | 11+N | край, `f32 relative`, `f32 relativeToScreen`, длина ключа (≤ 64), ключ монитора UTF-8 |
| `EdgeReachedOnMonitor` 10 | 11+N | то же |

Неизвестный вид или обрезанное событие останавливают разбор остатка кадра. `InputSender` (`src/WinMirroring.Input/InputSender.cs`): продюсеры только дописывают в буфер под замком; фоновый цикл шлёт накопленное одним кадром, а при тишине `AliveInterval = 250` мс — `Alive`. Переполнение `MaxPendingBytes = 256 КБ` или ошибка записи → `IsFaulted`, захват обрывается. `CompleteWithLeave()` дописывает `Leave` и завершает цикл; `Abort()` выбрасывает очередь. Записи в сокет не отменяются (полузаписанная TLS-запись испортила бы сессию).

**Сообщения.** Канал `ChannelId.Input`: виды 1–6, 8, 9 идут от управляющего; 7, 8, 10 — от управляемого.

**Ограничения и запасные пути.** Поля версии протокола в канале нет; совместимость со старыми устройствами — через признак «прислал `InputMonitorsMessage`».

**Тесты.** `InputCodecTests`.

### Воспроизведение на управляемой стороне и выход курсора через край

**Что делает.** Чужое движение проигрывается плавно, клики попадают туда, где был курсор в момент нажатия; когда курсор выталкивают за край, управление уходит обратно или дальше.

**Как устроено.** `OnInputFrameAsync` → `Apply` (`InputSharingService.Controlled.cs`). `Enter*` → `TakeControl`: точка входа — центр основного монитора или `Snap(PointOn(названный монитор или весь экран, edge, relative, inset: 1))`; `_player.Begin`; запуск `KeepControlAliveAsync`; `ControlledByChanged(peerId)`. Движение/кнопки/колесо/клавиши исполняются, только пока `state.Active`.

`RemoteCursorPlayer` (`src/WinMirroring.Input/Injection/RemoteCursorPlayer.cs`): один владелец курсора (последний `Enter` забирает его; события прежнего владельца игнорируются). Позиция ведётся самим плеером (`_tracked`), а не читается из `GetCursorPos` после каждой инъекции; сверка с системой только после `ResyncAfterIdleMs = 150` мс простоя. Движение проходит через `PlayoutScheduler` и очередь; поток `WinMirroring.CursorPlayback` (приоритет AboveNormal) суммирует все созревшие отсчёты в одно абсолютное перемещение. Дискретные события (`Execute`) сначала доигрывают всю очередь. Движение, уводящее за пределы всех мониторов, упирается в край текущего. `DetectEdge` копит выход за край в пикселях и при `EdgeResistance` поднимает `EdgeReached(PlayerEdge)`.

Передача: `HandOffAsync` (не более одной одновременно): если зажата левая кнопка и задан `BeforeEdgeHandOff`, ждёт его до 1 с (перетаскивание файлов/окна), затем шлёт `EdgeReachedOnMonitor` (управляющему, знающему мониторы) или простой `EdgeReached`.

**Настройки.** `Input.EdgeResistance` — тот же параметр, но здесь сравнивается с накопленным выходом за край в пикселях, а не с сырыми отсчётами.

**Ограничения и запасные пути.** Ошибка в цикле воспроизведения логируется, поток продолжает.

**Тесты.** Нет (кроме `PlayoutSchedulerTests`).

### Тайминг и сглаживание задержки

**Что делает.** Адаптивный буфер скрывает неравномерность доставки: движение проигрывается с исходными интервалами ценой нескольких миллисекунд; текущая величина буфера показывается в интерфейсе («Сглаживание: N мс»).

**Как устроено.** `MonotonicClock` (`Timing/MonotonicClock.cs`): микросекунды от `Stopwatch`; `NowWire()` — младшие 32 бита (оборот ≈ 71 мин). `PlayoutScheduler` (`Timing/PlayoutScheduler.cs`): оценивает минимальное смещение «приход − захват» и пик джиттера над ним; задержка = пик + `MarginMicroseconds = 1000`, не более `MaxDelayMicroseconds`; минимум подрастает на 0,5 мс/с (дрейф часов, смена маршрута), пик спадает на 10 мс/с; после 10 с тишины — пересинхронизация; время воспроизведения не убывает; при `Enabled = false` — сразу по приходу. `PrecisionSleeper` (`Timing/PrecisionSleeper.cs`): таймер высокого разрешения (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`), сон не длиннее 2000 мкс за итерацию.

**Настройки.** `Input.SmoothingEnabled` = `true`; `Input.MaxSmoothingDelayMs` = 30 (плеер зажимает 0…200). Применяются `ApplyInputSettings()`; действуют на управляемом устройстве.

**Ограничения и запасные пути.** Нет таймера высокого разрешения → `timeBeginPeriod(1)` + `Thread.Sleep`.

**Тесты.** `PlayoutSchedulerTests`.

### Инъекция ввода: мышь, клавиатура, текст, касания

**Что делает.** Единая точка, через которую в систему попадают чужие мышь, клавиши, набранный текст и касания — и для общего ввода, и для управления из окна просмотра экрана.

**Как устроено.** `IInjectionSink` (`Injection/IInjectionSink.cs`): `MoveAbsolute`, `MoveRelative`, `Button`, `Wheel`, `Key`, `Unicode`, `TouchFrame`, `IsConnected`. `LocalInjectionSink` (`Injection/LocalInjectionSink.cs`): `SendInput` с `InjectionMarker` в `dwExtraInfo`. Абсолютное движение — `MOVE|ABSOLUTE|VIRTUALDESK`, нормализация `ceil(p·65536/size)`, чтобы Windows попадала ровно в пиксель. Клавиша: при `scanCode != 0` — `KEYEVENTF_SCANCODE` (+`EXTENDEDKEY`), иначе по `wVk`. `Unicode` — `KEYEVENTF_UNICODE`. Касания: `InitializeTouchInjection(10, TOUCH_FEEDBACK_DEFAULT)` лениво, затем `InjectTouchInput` кадром всех контактов (радиус 2 px, давление 32000, ориентация 90).

`InputInjector` (`Injection/InputInjector.cs`) — политика поверх стока: учёт зажатых клавиш (`vk → scan, extended`) и кнопок, `ReleaseAll()`, `ButtonUntracked`, `IsButtonDown`, `Nudge()` (абсолютно на пиксель и обратно — чтобы цикл drag-and-drop перепроверил окно под курсором), `Wiggle()` (относительно ±1 — возвращает курсор, скрытый Windows после касаний), `TypeText(text)` (посимвольно Unicode-пакетами; `\r`/`\n` → VK_RETURN). По умолчанию сток — `InjectionRouter.Instance`. Экземпляры: в `InputSharingService`, в `ScreenSource` (`src/WinMirroring.Screen`), в `ScreenViewerWindow.Compose`; касания шлёт `WinMirroring.Screen/Injection/TouchInjector.cs` через тот же роутер.

**Ограничения и запасные пути.** Инъекции пера в коде нет (ни `InjectSyntheticPointerInput`, ни PT_PEN). `TypeText` и `TouchFrame` общим вводом через край не используются — только управлением из окна просмотра. Касания недоступны на устройстве → одно предупреждение в журнал, кадры отбрасываются.

**Тесты.** `InjectionCodecTests` (через записывающий сток).

### Клавиатура: скан-коды, особые клавиши, возврат по Ctrl+Alt+Home

**Что делает.** Клавиши идут скан-кодами: символ определяет раскладка управляемого устройства. Системные сочетания (Win, Alt+Tab и т. п.) при захвате срабатывают на управляемом устройстве. Ctrl+Alt+Home возвращает ввод домой.

**Как устроено.** При захвате `OnKeyboardHook` проглатывает и пересылает всё, что видит `WH_KEYBOARD_LL`, без списка исключений. Единственное сочетание, обрабатываемое локально: нажатие `VK_HOME (0x24)` при физически зажатых Ctrl (`0x11/0xA2/0xA3`) и Alt (`0x12/0xA4/0xA5`) → `ReturnHomeCommand`, событие проглатывается и не пересылается. Это не `RegisterHotKey`: работает только пока ввод отдан, не настраивается.

**Ограничения и запасные пути.** Особой обработки Ctrl+Alt+Del, Win+L и прочих сочетаний, которые система не отдаёт LL-хуку, в коде нет. Клавиши без скан-кода (scan = 0) вводятся по виртуальному коду. Других горячих клавиш в этой области нет.

**Тесты.** Нет.

### Залипшие клавиши и кнопки: как отпускаются

**Что делает.** Ни на одном устройстве клавиша или кнопка не остаётся «зажатой» после перехода, обрыва связи или падения процесса.

**Как устроено.** (1) Управляющий: отпускание клавиши/кнопки, зажатой до начала захвата, и пересылается, и пропускается локально (`_passThroughKeyUps`/`_passThroughButtonUps`). (2) Управляемый: `ReleaseControl` (на `Leave`, закрытие сессии, тишину, «вернуть курсор», завершение работы) вызывает `ControlEnding`, затем `_player.End` и `_injector.ReleaseAll()` — отпускает всё, что было зажато этим инжектором. (3) Тишина управляющего дольше `ControlledSilenceMs = 2000` мс (проверка каждые 250 мс в `KeepControlAliveAsync`) → то же. (4) Помощник: `TrackingInjectionSink` (`Injection/TrackingInjectionSink.cs`) отпускает зажатое при обрыве канала или `Bye`. (5) `InjectLocalButton(button, down, track)` — для перетаскивания: отслеживаемое нажатие снимется само при уходе управляющего, неотслеживаемое — на совести вызывающего. `IsLocalButtonPhysicallyDown` отдаёт физическое состояние кнопки даже во время захвата.

**Ограничения и запасные пути.** `ReleaseAll` выполняется, только если уходящая сессия — текущий владелец курсора.

**Тесты.** `InjectionCodecTests.TrackingSinkReleasesWhatIsStillHeld`.

### Аренда захвата и keepalive

**Что делает.** Локальный ввод не становится заложником сети: если управляемое устройство замолчало, первое же движение или нажатие возвращает ввод домой.

**Как устроено.** Захват — аренда. Управляемый каждые `AliveInterval = 250` мс шлёт кадр `Alive` (не более одного в полёте). Хук перед каждым событием проверяет `IsCaptureHealthy`: отправитель не в ошибке и с последнего приёма чего угодно от пира (`PeerSession.LastReceivedTicks`, но не раньше начала захвата) прошло не более `CaptureLeaseMs = 1500` мс. Иначе `DropCapture`: `_capture` обнуляется прямо в потоке хука (событие уже идёт локально), уборку делает воркер (`CaptureLostCommand`, без `Leave`). Управляющий со своей стороны шлёт `Alive` при тишине 250 мс (`InputSender`).

**Сообщения.** `Alive` (вид 8) в обе стороны.

**Тесты.** Нет.

### «Вернуть курсор» и возврат ввода

**Что делает.** Один пункт меню/кнопка: если ввод этого устройства ушёл — забирает его домой; если этим устройством управляют — перестаёт слушаться и просит управляющего забрать ввод (удобно на планшете касанием).

**Как устроено.** `ReturnCursor()` возвращает строку для показа: при `IsCapturing` — `ReturnHome()` («Ввод возвращён на это устройство.»); иначе каждому активному управляющему шлёт `InputReleaseRequestMessage` и сразу делает `ReleaseControl` («Курсор возвращён на {имя}.»); если ничего не было — `null`. Управляющий на сообщение завершает захват с `Leave` (`ReleaseRequestedCommand`). Вызывается из трея, панели, окна просмотра, `HubUi`.

**Сообщения.** `InputReleaseRequestMessage` — `"input-release"`.

**Ограничения и запасные пути.** Если управляющий сообщения не понял/не получил — его аренда истечёт за 1,5 с, так как управляемый перестал слать `Alive`.

**Тесты.** Нет.

### Синхронизация раскладки клавиатуры (языка ввода)

**Что делает.** Когда ввод уходит на другое устройство, там включается текущий язык пользователя; переключение языка там запоминается и применяется здесь после возврата. Окно просмотра экрана пользуется тем же механизмом.

**Как устроено.** `InputSharingService.KeyboardLayout.cs`, `Keyboard/KeyboardLayouts.cs`, `Keyboard/KeyboardLayoutFilter.cs`. Раскладка — младшие 32 бита HKL. Управляющий при начале захвата шлёт раскладку активного окна (`TryGetForeground`); при прямой передаче третьему устройству — последнюю, о которой сообщило предыдущее (`_captureLayout`). Получатель: `KeyboardLayouts.Match` (та же → тот же язык → тот же базовый язык, маска `0x3FF`), `Activate` шлёт `WM_INPUTLANGCHANGEREQUEST (0x0050)` элементу с фокусом, при неудаче — окну; результат `Requested/AlreadyActive/NoWindow/Refused`. Управляемый опрашивает раскладку активного окна каждые 150 мс (без управляющего — раз в 1 с) и через `KeyboardLayoutFilter` сообщает только о переключениях самого пользователя: отсеиваются собственное переключение по запросу (`Expect`, окно `ExpectMs = 1500`) и смена активного окна с другой раскладкой. Управляющий, получив сообщение от устройства, где сейчас его ввод, только запоминает раскладку и применяет её в `OnCaptureEndedForLayout`. `PushKeyboardLayout(peerId, layout)` — для окна просмотра. `OfCurrentThread`, `Installed`, `DisplayName` — вспомогательные.

**Настройки.** `Input.SyncKeyboardLayout` = `true` (`SetSyncKeyboardLayout`).

**Сообщения.** `KeyboardLayoutMessage(Layout)` — `"kbd-layout"`, в обе стороны.

**Ограничения и запасные пути.** Нет подходящей установленной раскладки → `KeyboardLayoutMissing(имя языка)`, не чаще раза в 30 минут на язык. Окно администратора на переднем плане отклоняет запрос (`Refused`). Нет активного окна (экран блокировки, UAC) → `NoWindow`.

**Тесты.** `KeyboardLayoutTests`.

### Окна администратора: обнаружение блокировки (InjectionGuard)

**Что делает.** Если на управляемом устройстве активно окно с повышенными правами, Windows молча отбрасывает инжектированный ввод. Курсор при этом можно увести обратно через край, а управляющий получает уведомление с заголовком мешающего окна.

**Как устроено.** `InjectionGuard` (`src/WinMirroring.Input/Native/InjectionGuard.cs`): `IsElevated` (уровень целостности процесса ≥ `0x3000`), `IsForegroundAboveUs(out title)` (уровень процесса активного окна выше нашего; если процесс нельзя открыть — считается выше, когда мы сами не повышены), `IsCursorSuppressed()` (`CURSOR_SUPPRESSED`). `RemoteCursorPlayer` после простоя, увидев расхождение позиции: если впереди повышенное окно — сохраняет свою отслеживаемую позицию (края продолжают работать) и поднимает `InjectionBlocked` не чаще раза в `BlockedReportIntervalMs = 10 000` мс; иначе принимает системную позицию (локальная мышь/касание) и при скрытом курсоре делает `Wiggle`. То же при `Begin`.

**Сообщения.** `InputBlockedMessage(Window)` — `"input-blocked"`, управляемый → управляющий; там событие `RemoteInputBlocked(peerName, title)` и уведомление. То же сообщение шлёт `ScreenSource`.

**Тесты.** Нет.

### Управление окнами администратора: помощник InputHost

**Что делает.** По опции отдельная маленькая программа с правами администратора выполняет инъекцию за приложение, и ввод с других устройств доходит до окон администратора. Само приложение остаётся с обычными правами, поэтому перетаскивание и вставка файлов продолжают работать. Запрос UAC можно заменить заданием Планировщика, чтобы он не появлялся при каждом запуске.

**Как устроено.**
- `InjectionRouter` (`Injection/InjectionRouter.cs`): общий на процесс сток; каждый вызов идёт в удалённый сток, если он предпочтён и подключён, иначе в локальный. `UsingRemote`, `SetRemote(sink, prefer)`.
- `ElevatedInjector` (`Injection/ElevatedInjector.cs`, singleton в DI приложения): `EnableAsync` создаёт сервер именованного канала, запускает помощника — сначала через задание (`ElevatedHelperTask.TryStart`), иначе `runas` с аргументами `--inject-pipe <имя> --inject-token <hex>`; ждёт подключения (25 с через задание, 90 с с запросом UAC), проверяет, что клиент — exe помощника рядом с приложением, обменивается рукопожатием с 32-байтным случайным токеном, ставит `RemoteInjectionSink` в роутер. `Disable()`, `StateChanged(active, detail)`, `IsActive`, `HelperPath`, `StartsWithoutPrompting`, `AllowStartingWithoutPrompting(out error)`, `RequirePromptAgain(out error)`. Уборка: `Bye`, закрытие канала, ожидание помощника 1,5 с, затем `Kill`.
- `InjectionPipe` (`Injection/InjectionPipe.cs`): имя канала постоянное на пользователя — `WinMirroring.Inject.{SID}` (задание не может передать аргументы); ожидаемые пути `WinMirroring.exe` и `WinMirroring.InputHost.exe` в `AppContext.BaseDirectory`; проверка собеседника — `Core/Local/PipePeer` (PID другого конца канала → полный путь образа, сравнение без учёта регистра). Сервер — приложение (средняя целостность), клиент — помощник.
- `RemoteInjectionSink` (`Injection/RemoteInjectionSink.cs`): сериализует вызовы в канал под замком; первая ошибка записи помечает сток отключённым и поднимает `Disconnected` → роутер возвращается к локальной инъекции, `StateChanged(false, «соединение с помощником потеряно»)`.
- `InjectionCodec` (`Injection/InjectionCodec.cs`): кадр = `i32` длина (1…`MaxMessageLength = 64 КБ`) + сообщение; опкоды `Handshake=1` (+32 байта токена), `MoveAbsolute=2` (2×`i32`), `MoveRelative=3`, `Button=4`, `Wheel=5` (`i32`, флаг), `Key=6` (`u16 vk`, `u16 scan`, up, extended), `Unicode=7` (`u16`, up), `TouchFrame=8` (счётчик ≤ 255, по 16 байт на контакт: id, x, y, flags), `Bye=9`. Координаты — физические пиксели; нормализует сток помощника.
- `InjectionHelperHost` (`Injection/InjectionHelperHost.cs`): тело помощника. Подключается к каналу (15 с), проверяет, что сервер — `WinMirroring.exe` рядом, читает рукопожатие (токен сверяется, только если был передан аргументом; при запуске через задание возвращается любой), затем применяет сообщения через `TrackingInjectionSink(LocalInjectionSink)` до `Bye`/обрыва и отпускает зажатое. Коды выхода: 2 — плохой токен в аргументах, 3 — не подключился, 4 — сервер не наше приложение, 5 — рукопожатие, 0 — норма.
- `ElevatedHelperTask` (`Injection/ElevatedHelperTask.cs`): задание «WinMirroring input helper ({пользователь})» через `schtasks.exe`: без триггеров, `InteractiveToken`, `HighestAvailable`, `MultipleInstancesPolicy = Parallel`, без ограничений по батарее, `ExecutionTimeLimit = PT0S`, без аргументов. `IsRegisteredFor(path)` сверяет путь в задании с помощником этой сборки; `Register`/`Unregister` — один запрос UAC; `TryStart` — без запроса.
- `src/WinMirroring.InputHost`: `Program.cs` — с аргументом `--shell-pipe` запускает `ElevatedShellHost` (консоль администратора, другая область), иначе `InjectionHelperHost`. Манифест: `highestAvailable`, `uiAccess="false"`, PerMonitorV2. Публикуется в папку приложения целью `PublishInputHost` в `WinMirroring.App.csproj`.

**Настройки.** `Input.ElevatedInjector` = `false`. При старте приложения с включённой опцией вызывается `EnableAsync()`; переключатель — `App.SetAdminWindowControlAsync`. В интерфейсе настроек: `input.adminControl`, команды `adminNoPrompt` / `adminPrompt`.

**Ограничения и запасные пути.** Нет exe помощника, отклонён UAC, не прошло рукопожатие, посторонний клиент — `EnableAsync` возвращает `false`, ввод остаётся локальным. Помощник, запущенный через задание, не является дочерним процессом: его завершение видно только по обрыву канала. Экран блокировки и защищённый рабочий стол UAC этим не покрываются (`uiAccess="false"`, служб нет). Запуск всего приложения от администратора — отдельный запасной вариант вне этой области.

**Тесты.** `InjectionCodecTests` (кодек, кадрирование, рукопожатие, `RouterPrefersTheRemoteOnlyWhileItIsConnected`, `TrackingSinkReleasesWhatIsStillHeld`); `ElevatedHelperTaskTests` (определение задания, `WindowsUnderstandsTheDefinition` реально вызывает `schtasks`, имя канала). Сам обход UIPI на двух устройствах тестами не покрыт.

### Аккорды клавиш (KeyChord)

**Что делает.** Разбор и запись сочетаний вида «Ctrl+Shift+T» и последовательностей «Ctrl+K, Ctrl+C» — для настраиваемых кнопок окна просмотра (панель кнопок, вне этой области).

**Как устроено.** `KeyChord(Modifiers, Key)` (`src/WinMirroring.Input/Keyboard/KeyChord.cs`): модификаторы приводятся к левым кодам (`Ctrl 0xA2`, `Shift 0xA0`, `Alt 0xA4`, `Win 0x5B`) в порядке Ctrl, Shift, Alt, Win; `Key = 0` — только модификаторы (`IsModifiersOnly`, «залипающая» кнопка). `TryParse`/`TryParseSequence` (ошибки по-русски; «Ctrl++» = Ctrl и плюс; две обычные клавиши в одном аккорде — ошибка), `ToString`/`FormatSequence`, `NameOf`, `AsModifier`, `IsExtended(vk)` (навигационный блок, правые модификаторы, Win, мультимедиа). Имена: буквы, цифры, Num0–9, F1–F24, Esc, Tab, Enter, Space/«Пробел», стрелки, знаки, мультимедиа, RCtrl/RShift/RAlt/RWin и синонимы. Потребители: `ButtonBarService`, `ButtonPresetsWindow`, `ScreenViewerWindow`.

**Тесты.** `KeyChordTests`.

### Точки стыка с другими модулями

**Как устроено.** Перетаскивание файлов и окон (`DragDropCoordinator`, `ScreenStreamingService`): `BeforeEdgeHandOff`, `ControlEnding`, `IsLocalButtonPhysicallyDown`, `InjectLocalButton`, `NudgeCursor`, `MoveCursorTo`, `CursorParkPoint`. Второй монитор: `IsCrossingSuppressed`, `SideOf`, `MonitorCatalog.VirtualMonitorIsHidden`. Звук следует за вводом: `ControlledByChanged`. Интерфейс: `FocusChanged`, `LayoutChanged`, `MonitorsChanged`, `PlayoutDelayMilliseconds`, `RemoteInputBlocked`, `KeyboardLayoutMissing`.

### Сводка настроек (`InputSettings`)

| Свойство | По умолчанию | Действие |
|---|---|---|
| `Enabled` | `true` | Хуки и роль управляющего |
| `EdgeResistance` | `8` | Усилие для перехода через край (обе стороны) |
| `SmoothingEnabled` | `true` | Буфер джиттера на управляемом |
| `MaxSmoothingDelayMs` | `30` | Потолок буфера (0…200) |
| `SyncKeyboardLayout` | `true` | Синхронизация языка ввода |
| `ElevatedInjector` | `false` | Инъекция через помощника с правами администратора |
| `LayoutVersion` / `LayoutOrigin` | `0` / `""` | Версия и автор раскладки |
| `Layout` / `Placements` | пусто | Сетка / свободная расстановка |

Константы, не вынесенные в настройки: `CaptureLeaseMs 1500`, `ControlledSilenceMs 2000`, `UsableSilenceMs 8000`, `AliveInterval 250 мс`, `MaxPendingBytes 256 КБ`, `ResyncAfterIdleMs 150`, `BlockedReportIntervalMs 10000`, `EdgeTolerance 48`, `MonitorCatalog.MaxAgeMs 1000`, `LayoutPollInterval 150 мс` / `IdleLayoutPollInterval 1 с`, `KeyboardLayoutFilter.ExpectMs 1500`, `MissingLayoutNoticeMs 30 мин`.

### Тесты области (`tests/WinMirroring.Input.Tests`)

`ElevatedHelperTaskTests`, `FreeLayoutTests`, `InjectionCodecTests`, `InputCodecTests`, `KeyboardLayoutTests`, `KeyChordTests`, `LayoutAndEdgeTests`, `LayoutRelayIntegrationTests`, `LayoutVersionTests`, `MonitorLayoutTests`, `PlayoutSchedulerTests`. Единственный `[Fact(Explicit = true)]` — `MonitorLayoutTests.TheCatalogSeesThisMachinesMonitors`. Без тестов: `InputHookThread`, `InputSender`, `RemoteCursorPlayer`, `InputInjector`, `LocalInjectionSink`, `ElevatedInjector` (сквозной запуск), `InjectionGuard`, логика захвата и аренды в `InputSharingService`.
