# Трансляция экрана: служба, виды и приём (WinMirroring.Screen)

> Часть справочника [docs/FEATURES.md](../FEATURES.md). Составлено по коду версии 1.0.75; источник правды — код.

Охват: `src/WinMirroring.Screen/ScreenStreamingService.cs` (+ `.Display.cs`, `.WindowMove.cs`), `ScreenViewer.cs`, `RemoteInputTracker.cs`, `ViewParts.cs`, `ViewEndings.cs`, `MonitorNeighbours.cs`, папки `Display/` и `Injection/`. Обработчики сообщений зрителя на стороне источника живут в `ScreenSource.cs` (управление, указатель, миниатюры и т. п.) и описаны здесь только с этой стороны; захват, кодеки, `SendWindow`/`StreamPacing`, `VideoLink`/`VideoReassembly` и формат `ScreenCodec` — в соседнем разделе. Всё ниже сверено с кодом на коммите 1.0.75; тесты не запускались.

Термины: **источник** — устройство, чей экран/окно показывают (`ScreenSource`, внутренний класс); **зритель** — устройство, которое показывает (`ScreenViewer`). `StreamKind`: `Monitor`, `Window`, `Display` (виртуальный монитор, «второй монитор»); в `ScreenStreamMessage.Kind` это строки `"monitor"`, `"window"`, `"display"`.

### Служба ScreenStreamingService: состав и жизненный цикл

**Что делает.** Один экземпляр на приложение. Держит все исходящие потоки (`_sources`) и все входящие виды (`_viewers`) по `StreamId`, разбирает управляющие сообщения и канал `ChannelId.Screen`, сообщает приложению об открытии вида и об изменении набора потоков.

**Как устроено.** `Start()` подписывается на `MirroringNode.Sessions.SessionReady/SessionClosed`, `MirroringNode.MediaPacketReceived` (UDP) и `WinMirroring.Input.Screen.MonitorCatalog.Changed`; для уже готовых сессий сразу вызывает `OnSessionReady`. На готовой сессии регистрируется обработчик канала Screen (`OnScreenFrameAsync`) и `ControlMessageReceived`. Словари: `_sources` (uint → `ScreenSource`), `_viewers` (uint → `ScreenViewer`), `_requested` (StreamId → peerId: что мы просили), `_windowsStarting` ((hwnd, peerId): окно в процессе запуска потока), `_peerCaps` (peerId → `DeviceCapabilitiesMessage`). `StreamId` — случайное число 1..int.MaxValue (`NewStreamId`); при запросе монитора его выбирает зритель, при отправке окна и втором мониторе — источник. События: `ViewerOpened` (на рабочем потоке; приложение открывает `ScreenViewerWindow`), `SourcesChanged`, `ViewersChanged`, `ExtendedChanged`, `ExtendFailed`. Свойства: `Sources` (список `ActiveScreenSource(StreamId, PeerName, Title)`), `HasActivity` (есть источник или вид — приложение откладывает автообновление, `App.xaml.cs`), `IsStreamingScreen` (есть поток Monitor/Display — приложение не даёт дисплею уснуть, `App.Viewers.cs: UpdateKeepAwake`), `IsMovingWindow`. При закрытии сессии: её источники освобождаются без `ScreenStop` (+ `OnSourceRemoved`), её виды закрываются с `lostConnection: true`, запись `_peerCaps` удаляется. `DisposeAsync` идемпотентен: останавливает все источники с причиной `"shutdown"`, возвращает припаркованные окна, отключает виртуальный монитор (только если `_displayDevice` известен), освобождает виды.

**Сообщения.** Все перечисленные ниже; сводная таблица — в конце раздела.

**Ограничения и запасные пути.** Отправка `ScreenStop` при остановке ограничена 2 с (`StopNoticeWithin`): на умирающем соединении она могла не завершиться, и поток жил без зрителя. Изменение `AllowViewing` на ходу уже идущие потоки не останавливает — кода, реагирующего на смену настройки, нет.

**Тесты.** `ScreenStreamIntegrationTests` (два настоящих узла на loopback; 11 из 12 методов `[Fact(Explicit = true)]`, обычный один — `ExtendWithoutTheDriverIsRefusedWithAReason`).

### Обмен возможностями устройств

**Что делает.** Сразу после установления сессии устройства сообщают друг другу: есть ли драйвер виртуального монитора, какие у них мониторы, какие кодеки они умеют и рисуют ли они указатель сами. От этого зависят выбор кодека, пункты меню «второй монитор» и список мониторов для показа.

**Как устроено.** `DescribeCapabilities(reply)` собирает `DeviceCapabilitiesMessage(HasVirtualDisplay, IsReply, Monitors, CanEncodeH264, CanDecodeH264, DrawsPointer: true, CanEncodeHevc, CanDecodeHevc)`. Мониторы — `DisplayCatalog.Monitors()` без виртуального: `RemoteMonitor(Index, Label = "Ш×В", Primary, Key = имя дисплея \\.\DISPLAYn)`; порядок — основной первым, затем слева направо. Кодеки — из `Encoding/VideoCodecSupport` (ленивые пробы настоящего создания кодера/декодера, кэш на процесс), поэтому сообщение строится в `Task.Run` (`SendCapabilities`). Не-ответ (`IsReply = false`) получатель сохраняет в `_peerCaps` и отвечает своим с `IsReply = true` — так первое сообщение не теряется, если пришло до подписки. При смене мониторов (`OnMonitorsChanged`) всем готовым сессиям уходит новый список как ответ. Наружу: `PeerVirtualDisplay(peerId)` (bool?, null пока не сообщил), `PeerHasVirtualDisplay`, `PeerMonitors(peerId)`.

**Сообщения.** `DeviceCapabilitiesMessage` (`"caps"`).

**Ограничения и запасные пути.** Старый пир без полей получает значения по умолчанию `false` — ему идут JPEG-плитки и указатель, врисованный в кадр. В списке мониторов возможностей виртуального монитора нет, но `ScreenSource.PickMonitor`, `ScreenStreamMessage.MonitorCount` и цикл миниатюр берут `DisplayCatalog.Monitors()` целиком — при подключённом виртуальном мониторе счёт и выбор по индексу его включают.

**Тесты.** Отдельных нет; косвенно — `ScreenStreamIntegrationTests` (`ViewerReceivesTheMonitorAsVideo`, `…AsHevc`, Explicit), пробы — `VideoCodecTests`, `HevcTests`.

### Согласование кодека: H.264, HEVC, плитки и отступления

**Что делает.** Поток идёт аппаратным H.264, если это разрешено настройкой и умеют обе стороны; HEVC — только если он выбран в настройках; иначе JPEG-плитки. Если у зрителя из видео не выходит картинка, он просит источник отступить, и поток продолжается без переоткрытия.

**Как устроено.** При создании источника: `NegotiateVideo(session)` = `VideoCodec != Jpeg` И `VideoCodecSupport.CanEncodeH264` И `_peerCaps[peer].CanDecodeH264` → параметр `useVideo` (неизменяем на всю жизнь потока). `WithFormat` ставит `source.PreferHevc` = `VideoCodec == Hevc` И `CanEncodeHevc` здесь И `CanDecodeHevc` у пира. Значения `Auto` и `H264` ведут себя одинаково. Объявленный кодек — `ScreenStreamMessage.Codec` (`"jpeg"`, `"h264"` или имя HEVC из `VideoInterop.NameOf`). Зритель формат видео берёт не из описания, а из первых байт каждого ключевого кадра (`VideoInterop.Sniff`): описание идёт по соединению, кадры — датаграммами, не в ногу. Декодер создаётся в `ScreenViewer.DecodeVideoAsync`: `H264VideoDecoder.TryCreate(..., useGraphicsCard: format == Hevc || !_decodeInMemory, format)`; HEVC-декодер не на видеокарте отбрасывается (в памяти картинка неверна).

Отступления у зрителя (`RequestTiles(reason)` → `ScreenCodecFallbackMessage`, один раз на формат, флаг `_tilesRequested`): декодер не создался; видеокарта перестала выдавать кадры HEVC (`GraphicsCardFailed`); видео приходит, а картинки нет дольше `VideoSilenceBeforeFallbackMs = 3000`. Для H.264 при `GraphicsCardFailed` жалобы нет: декодер пересоздаётся на процессоре (`_decodeInMemory = true`). На источнике служба передаёт жалобу в `ScreenSource.FallBackToTiles(reason)`; цикл потока при первой жалобе на HEVC переходит на H.264 (`_hevcFailed`), при жалобе на H.264 — на плитки (`_videoFailed`), каждый раз заново шлёт `ScreenStreamMessage` и целый кадр. Сам источник: кодер HEVC не создался → сразу H.264; кодер H.264 не создался или упал при подаче → плитки. Обрезка меньше 16×16 идёт плитками покадрово.

**Настройки.** `VideoCodec` = `Auto`; `VideoBitrateKbps` = 12000 (в источнике зажимается 500..100000, масштабируется корнем из площади относительно 1920×1080 в пределах 0,25..1 и долей `StreamPacing.Share`); `Quality` = 70 (плитки, 30..95, тоже умножается на `Share`).

**Сообщения.** `ScreenCodecFallbackMessage(StreamId, Reason)` зритель → источник; `ScreenStreamMessage` повторно источник → зритель; типы канала `Frame = 1`, `VideoFrame = 9`.

**Ограничения и запасные пути.** HEVC требует, чтобы согласовался и H.264 (`useVideo`). Поток, созданный до прихода возможностей пира, остаётся на плитках и с врисованным указателем навсегда (`_useVideo`, `_sendsPointer` — `readonly`). В ARCHITECTURE помечено как непроверенное: отступление HEVC → H.264 по жалобе зрителя. По чтению кода оно в двух случаях не срабатывает: если жалоба ушла при отсутствующем декодере (не создался HEVC-декодер или карта отказала на HEVC), у зрителя остаётся `_videoDecodeUnavailable = true`, а сбрасывается он только в ветке «формат существующего декодера сменился» — декодера нет, H.264-декодер не создаётся, вторая жалоба не уходит (`_tilesRequested` уже 1); вид остаётся без картинки. Путь «HEVC-декодер есть, но 3 с молчит» работает.

**Тесты.** `HevcTests`, `VideoCodecTests`, `GpuDecodingTests` (2 Explicit), `TileAndCodecTests`; сквозные `ViewerReceivesTheMonitor` / `…AsVideo` / `…AsHevc` (Explicit). Отступления по жалобе зрителя тестами не покрыты.

### Запрос монитора: как вид просят, разрешают и отклоняют

**Что делает.** Зритель просит у сопряжённого устройства один из его мониторов; источник либо начинает поток и описывает его, либо отказывает с причиной. Монитор без запроса не показывается никогда; окно и второй монитор приходят по инициативе источника.

**Как устроено.** Зритель: `RequestScreenAsync(peerId, monitorIndex = 0, monitorKey = null)` — новый `StreamId` в `_requested`, отправка `ScreenRequestMessage(StreamId, MonitorIndex, MonitorKey, MaxFps)`; `MaxFps` заполняется только при экономии энергии (`WantedFps`). Источник (`HandleRequestAsync`, в `Task.Run`): при `AllowViewing == false` отвечает `ScreenStopMessage(StreamId, "просмотр экрана запрещён на устройстве")`; если поток с таким `StreamId` от этой же сессии уже есть — это переключение монитора; иначе `ScreenSource.ForMonitor(...)`, `ViewerFps = request.MaxFps`, `StartSourceAsync`: подписка `Ended → StopSourceAsync(id, reason)`, запись в `_sources`, отправка `source.Describe()` (`ScreenStreamMessage`), `source.Start()`, `RaiseSourcesChanged`. Монитор выбирает `ScreenSource.PickMonitor(index, key)`: по имени дисплея, затем по `Index` каталога, затем по позиции с зажимом.

Зритель (`HandleStreamInfo`): если вид с этим `StreamId` есть и сессия та же — `viewer.OnInfo(info)` (обновление описания и ключа UDP). Иначе вид создаётся, только если: `StreamId` был в `_requested` для этого пира, ИЛИ `Kind == "window"`, ИЛИ `Kind == "display"` и мы сами просили расширение у этого пира не более 30 с назад (`AcceptsDisplayFrom`, одноразово). Создаётся `ScreenViewer(session, info, logger, sendMedia)`, подписка на `Closed` убирает его из `_viewers`, поднимается `ViewerOpened`.

`ScreenStreamMessage`: `StreamId, Kind, Title, MonitorIndex, MonitorCount, ControlAllowed, Minimized, ScalePercent, Codec, MonitorKey, MediaKey, CropTags, MediaReplies`. `ControlAllowed` — снимок `AllowControl` на момент описания.

**Настройки.** `AllowViewing` = true; `AllowControl` = true; `MaxFps` = 60 (источник); `PowerSettings.Fps` = 30 и `PowerSettings.Saving` = `OnBattery` (чужой раздел, читаются через `MirroringNode.Power`).

**Сообщения.** `ScreenRequestMessage`, `ScreenStreamMessage`, `ScreenStopMessage`.

**Ограничения и запасные пути.** Непрошеный `"monitor"` молча игнорируется (источнику ничего не отвечается). Входящее окно принимается всегда — настройки «не принимать окна» на зрителе нет. Ошибки запуска (`IOException`, `COMException`, `ArgumentException`…) только пишутся в журнал; зритель ответа не получает, запись в `_requested` остаётся. README говорит «по умолчанию не больше 30 кадров/с» — в коде `MaxFps = 60`.

**Тесты.** `MonitorChoiceTests` (`PickMonitor`), `ScreenStreamIntegrationTests` (Explicit).

### Переключение монитора внутри вида и сосед по расположению

**Что делает.** В открытом виде можно перейти на другой монитор того же устройства без нового потока: тот же `StreamId`, тот же вид. Жест «три пальца вбок» выбирает монитор, который физически стоит с той стороны.

**Как устроено.** `ScreenViewer.SwitchMonitor(index, key)` шлёт `ScreenRequestMessage` со своим `StreamId`. Источник в `HandleRequestAsync` находит существующий поток: `existing.ViewerFps = request.MaxFps`, `existing.SwitchMonitor(index, key)` (только `Kind == Monitor`: новый `WgcCapture`, сброс обрезки и `_cropId`, новое название, принудительный целый кадр), затем заново `Describe()`. Приложение держит один вид на устройство (`App.Viewers.cs: ShowOrSwitchScreen`). `MonitorNeighbours` (чистая арифметика): `Beside(monitors, current, direction)` — ближайший по центру монитор слева/справа, при равенстве (±0,5 px) — с большим перекрытием по высоте; `IsSideways(dx, dy)` — |dx| ≥ 2·|dy|; `CommitsAfter(width)` — 12 % ширины в пределах 90..260 px. Расположение мониторов пира берётся из `InputSharingService.PeerMonitors` (не из `_peerCaps`), вызывает `ScreenViewerWindow.Paging`.

**Настройки.** `SwipeMonitors` = true (читает окно, не служба).

**Сообщения.** `ScreenRequestMessage` (повторно), `ScreenStreamMessage`.

**Ограничения и запасные пути.** `ScreenViewer.SwitchMonitor` не передаёт `MaxFps`, поэтому после переключения источник сбрасывает `ViewerFps` в null — ограничение кадров зрителя на батарее теряется до переоткрытия вида. Запрос на переключение при выключенном `AllowViewing` отправляет зрителю `ScreenStop`, но сам источник не останавливает: зритель закрывается с `notifySource: false`, поток остаётся жить до конца сессии или ручной остановки. Сам жест проверен только сборкой (ARCHITECTURE).

**Тесты.** `MonitorNeighbourTests`, `MonitorChoiceTests`, `SwitchingMonitorsUnderAStreamDoesNotEndIt` (Explicit).

### Остановка потока, конец вида и переоткрытие (ViewEndings, CanResume)

**Что делает.** Поток может закончить любая сторона. Если вид закончился не по чьей-то воле (обрыв связи, перезапуск приложения на источнике), окно остаётся с последним кадром и вид открывается заново, когда устройство вернётся.

**Как устроено.** Источник: `StopSourceAsync(streamId, reason = "stopped")` — удаляет из `_sources`, шлёт `ScreenStopMessage` (не дольше 2 с), `DisposeAsync`, `RaiseSourcesChanged`, `OnSourceRemoved`. Причины из кода: `"stopped"`, `"shutdown"` (выход приложения), `"второй монитор отключён"`, из `ScreenSource.Ended` — `"window closed"`, `"connection: …"`, `"error"`. Приём `ScreenStopMessage`: если это наш источник — освободить; если наш вид — `viewer.CloseAsync(notifySource: false, reason)`; запись `_requested` убирается. Зритель: `Close()`/`DisposeAsync()` → `CloseAsync(notifySource: true, null)` шлёт `ScreenStopMessage(StreamId, "closed by viewer")`. `CloseAsync` однократен: завершает очереди кадров и исходящих, отменяет `_cts`, поднимает `Closed(viewer, reason)`. Перед этим выставляется `CanResume = !notifySource && ViewEndings.CanResume(lostConnection, reason)`: true, если сессия закрылась под видом (`"соединение закрыто"`), цикл декодирования упал на вводе-выводе (`"соединение потеряно"`), либо причина источника — `"shutdown"`, `"error"` или начинается с `"connection"`. `ViewEndings.AllowAnother(earlier, now)`: не больше `MostInARow = 3` переоткрытий за `InARowWithin = 60 с`. Потребитель — `App.Resume.cs` (только `Kind == "monitor"`): первый повторный запрос через 0,7 с, затем каждые 3 с, не более 8 раз и 2 минут; новое окно перенимает состояние ввода и свёрнутость.

**Настройки.** `CloseUnusedViewMinutes` = 5 (читает `App.Viewers.cs`: свёрнутый вид монитора закрывается; 0 — никогда).

**Сообщения.** `ScreenStopMessage`.

**Ограничения и запасные пути.** `ScreenViewerWindow` при закрытии вызывает только `_viewer.Close()`; `ScreenViewer.DisposeAsync` вызывает лишь служба для видов, ещё лежащих в `_viewers`, а закрытый вид оттуда уже удалён. Поэтому у обычно закрытого вида не освобождаются `MediaTimerResolution.Hold()` (таймер 1 мс остаётся взятым до конца процесса) и `MediaCipher`; декодеры и поверхности освобождает сам цикл декодирования. Замечено по чтению кода.

**Тесты.** `ViewEndingTests`, `AViewThatLostItsSourceSaysItCanBeOpenedAgainAndCanBe` (Explicit).

### Отправка окна на другое устройство

**Что делает.** Одно окно этого устройства показывается на другом как отдельное окно с управлением. Если то устройство уже служит вторым монитором, вместо копии туда переезжает настоящее окно.

**Как устроено.** `ShareableWindows()` → `DisplayCatalog.Windows()` (видимые, не свёрнутые, с заголовком, не tool-окна, чужих процессов, больше 40×40). `TransferWindowAsync(peerId, hwnd)`: если `IsExtendedTo(peerId)` и `TryMoveToDisplay(hwnd)` — окно просто переставляется на виртуальный монитор, потока нет; иначе `SendWindowAsync(peerId, hwnd, hideSource: true)`. `SendWindowAsync`: требует `AllowViewing`, готовую сессию и `IsWindow`; повтор того же окна тому же пиру (уже есть поток или ключ в `_windowsStarting`) возвращает true без второго потока; `ScreenSource.ForWindow` (ошибки захвата → false); `StartSourceAsync`; при успехе, `hideSource` и `HideSentWindow` — `ParkOnVirtualMonitorAsync`. Зритель принимает поток `"window"` без запроса. Источник следит за окном: закрылось → `End("window closed")`; свернули/развернули → повторный `ScreenStreamMessage` с `Minimized`; `ScalePercent` — масштаб окна. Зритель может прислать `ScreenMessageType.Window` с `WindowCommand.Restore` — источник разворачивает и выводит окно вперёд. Перед нажатием из вида окно выводится на передний план (`BringWindowForward`). В приложении (`MainViewModel.Screen.cs: SendWindowToAsync`) вслед за окном туда же уходит ввод (`_input.ControlPeer`), кроме случая второго монитора.

**Настройки.** `AllowViewing`, `HideSentWindow` = true.

**Сообщения.** `ScreenStreamMessage` (Kind `"window"`), тип канала `Window = 8`, `ScreenStopMessage`.

**Ограничения и запасные пути.** Текст статуса «с этого экрана скрыто» выводится по одной настройке `HideSentWindow`, даже когда драйвера нет и окно осталось на месте.

**Тесты.** `WindowPlacerTests` (куда встаёт окно). Самой отправки окна тесты не покрывают.

### Перетаскивание окна через край экрана

**Что делает.** Окно тянут за заголовок и уводят курсор через край к соседнему устройству — окно открывается там. Работает в обе стороны: с управляющего устройства на управляемое и обратно.

**Как устроено.** `AttachWindowDrag(input)` создаёт `Capture/WindowDragWatcher`: отдельный поток с WinEvent-хуком `EVENT_SYSTEM_MOVESIZESTART/END` (вне контекста, свой процесс пропускается), помнит перемещаемое окно (`Moving`) и его начальный прямоугольник. Путь 1 (это устройство управляет): `InputSharingService.FocusChanged(peerId)` → `OnFocusChanged`: если окно в перемещении и левая кнопка физически зажата, и такого потока ещё нет — `SendWindowAsync(peerId, moving)`. Путь 2 (этим устройством управляют): `DragDropCoordinator.HandOffAsync` вызывает `TrySendMovingWindowAsync(controllerPeerId)` перед передачей файлов. В обоих путях до отправки решается судьба настоящего окна: `ShouldHideDraggedWindow(peer)` = `HideSentWindow` И не `IsExtendedTo(peer)` И драйвер найден → `MarkParkOnRelease` (при отпускании `ReleasedForPark` → `ParkOnVirtualMonitorAsync`), иначе `MarkSent` (при отпускании окно ставится на начальную позицию `SetWindowPos` с `SWP_NOSIZE`).

**Настройки.** `WindowDragEnabled` = true; `AllowViewing`; `HideSentWindow`.

**Сообщения.** Те же, что у отправки окна.

**Ограничения и запасные пути.** Хук не поставился — в журнале «dragging windows to other devices is off», остальное работает. `TrySendMovingWindowAsync` возвращает true, не дожидаясь результата отправки. Парковка помечается до того, как известно, запустился ли поток: если `SendWindowAsync` не удался или поток закончился раньше, чем отпущена кнопка, окно всё равно уедет на виртуальный монитор, а вернуть его сможет только «Вернуть окна» или выход (возврат привязан к удалению источника). README и ARCHITECTURE пишут «окно вернётся на место» — это верно только без драйвера или при выключенном `HideSentWindow`.

**Тесты.** Нет (ARCHITECTURE: вживую не проверено).

### «Скрывать отправленное окно»: парковка на виртуальном мониторе

**Что делает.** Пока окно показывается копией на другом устройстве, настоящее окно убирается с экранов этого устройства на виртуальный монитор, где оно продолжает рисоваться и принимать ввод. Когда показ заканчивается, окно возвращается на прежнее место и в прежнее состояние.

**Как устроено.** `ScreenStreamingService.WindowMove.cs`. `_movedWindows` (hwnd → `MovedWindow(Rect, Maximized)`), `MovedWindowCount`. `ParkOnVirtualMonitorAsync`: пропуск, если окно уже перенесено или виртуальный монитор занят как второй монитор (`ExtendedTo != null`); отменяет отложенное отключение; `EnsureVirtualMonitorAttachedAsync` (под `_displayGate`): если монитор не на рабочем столе — режим под основной экран (`PickMode`, иначе Ш×В@60), позиция справа, `Attach`, при неудаче `ExtendTopology`, ожидание до 5 с. `MoveWindowOnto`: `TryCaptureHome` (развёрнутое окно сначала восстанавливается), цель — `WindowPlacer.PlaceInside(home, рабочая область монитора)` (тот же размер по центру, не больше монитора), `SetWindowPos`. Возврат: `OnSourceRemoved` для потока окна → `RestoreWindow(hwnd)` + `DetachVirtualMonitorWhenIdleAsync` — монитор отключается через `KeepMonitorFor = 5 мин`, если на нём ничего нет и он не второй монитор (чтобы экраны не мигали при каждой отправке). `RestoreMovedWindows()` — всё разом: при конце второго монитора, при выходе, по кнопке «Вернуть окна» (`MainViewModel.ReturnWindows`). `RestoreTo`: `SetWindowPos` на прежний прямоугольник, затем `SW_MAXIMIZE`, если было развёрнуто.

**Настройки.** `HideSentWindow` = true.

**Ограничения и запасные пути.** Без драйвера — тихий no-op, окно остаётся видимым. `TryMoveToDisplay` не проверяет, что окно уже в `_movedWindows`: повторный перенос перезапишет «дом» позицией на виртуальном мониторе.

**Тесты.** `WindowPlacerTests`; остальное не покрыто.

### Второй монитор: расширение рабочего стола на другое устройство

**Что делает.** Устройство-хост расширяет свой рабочий стол на экран другого устройства: в Windows появляется настоящий дополнительный монитор, его картинка идёт потоком `"display"`, зритель показывает её на весь экран с касаниями. Одновременно — только одно устройство.

**Как устроено.** `ScreenStreamingService.Display.cs`. Хост зовёт: `InviteAsDisplayAsync(peer)` → `DisplayInviteMessage`. Получатель без вопросов отвечает `RequestExtendAsync(hostPeer)`: `VirtualDisplay.PrimaryScreen()` (физические пиксели основного экрана и масштаб), отметка в `_displayRequested`, `DisplayExtendRequestMessage(Width, Height, ScalePercent)`; тот же метод вызывается напрямую пунктом «Стать вторым монитором». Хост (`ExtendToAsync` → `TryExtendLockedAsync` под `_displayGate`), отказ возвращается строкой-причиной: `AllowViewing` выключен; уже расширено на другое устройство (на то же — успех без действий); драйвер не найден; размер зажимается 640..8192 × 480..8192; `PickMode` по режимам драйвера; если точного нет и задан `AddDisplayModeAsync` — один раз на размер за запуск (`_modesAttempted`) добавление режима с UAC, затем ожидание устройства до 15 с; режим по умолчанию Ш×В@60; сторона — `SideOf(peer)` (из раскладки ввода, иначе `"right"`), позиция `PlaceBeside`; `Attach`, при отказе `ExtendTopology` + 1 с + повтор (неудача повтора только в журнале); ожидание монитора в каталоге до 5 с; `TrySetScale`; `ScreenSource.ForDisplay` (название «Второй монитор {имя машины}») и `StartSourceAsync`. Успех → `ExtendedChanged(peerId)`; отказ → `ExtendFailed(peerName, причина)` у себя и `DisplayExtendResultMessage(false, причина)` пиру (у него тоже `ExtendFailed`). Остановка: `StopExtendingAsync()` (причина «второй монитор отключён») или конец потока; `OnSourceRemoved` возвращает окна, отключает монитор, поднимает `ExtendedChanged(null)`. `ExtendedTo`, `IsExtendedTo(peer)`; приложение подключает `input.IsCrossingSuppressed = screen.IsExtendedTo` (переход курсора на это устройство как на отдельный компьютер выключен). `DetachLeftoverDisplay()` при старте снимает монитор, оставшийся после сбоя.

**Настройки.** `AllowViewing`.

**Сообщения.** `DisplayInviteMessage`, `DisplayExtendRequestMessage`, `DisplayExtendResultMessage` (шлётся только с `Ok = false`), далее обычный `ScreenStreamMessage` с Kind `"display"`.

**Ограничения и запасные пути.** Приглашение принимается автоматически, без подтверждения и без проверки настроек на приглашённом устройстве. Звук за таким просмотром не следует (`ViewersChanged` считает только `Monitor`). С установленным драйвером на машине разработки не проверено (ARCHITECTURE).

**Тесты.** `ExtendWithoutTheDriverIsRefusedWithAReason` (обычный `[Fact]`), `VirtualDisplayTests`.

### Драйвер виртуального монитора (папка Display)

**Что делает.** Используется сторонний открытый IddCx-драйвер Virtual Display Driver. Он держит один монитор постоянно; приложение лишь подключает его к рабочему столу и отключает, а при нехватке разрешения дописывает его в настройки драйвера. Драйвер нужен только устройству, чей экран расширяется или которое прячет отправленные окна.

**Как устроено.** `Display/VirtualDisplay.cs` (статический, без повышения прав): `FindDeviceName()` — перебор `EnumDisplayDevicesW`, признак: `DeviceString` содержит «Virtual Display» или `DeviceID` содержит «MttVDD»; null = драйвера нет (`IsVirtualDisplayAvailable`). `IsAttached`. `Modes(device)` — по одному режиму на размер, предпочтительно наибольшая частота ≤ 60 Гц. `PickMode(modes, w, h, out exact)`: точный размер; иначе та же форма (соотношение сторон отличается меньше чем на 0,02) не больше экрана, наибольшая по площади; иначе ближайшая форма, затем ближайшая площадь. `Attach(device, mode, position, out code)` — `ChangeDisplaySettingsExW` (32 бит, `CDS_UPDATEREGISTRY | CDS_NORESET`) и применяющий вызов. `ExtendTopology()` — `SetDisplayConfig(SDC_APPLY | SDC_TOPOLOGY_EXTEND)`. `Detach` — никогда не снимает основной экран. `PlaceBeside(others, size, side)` — `"right"|"left"|"top"|"bottom"`, выравнивание по основному. `PrimaryScreen()` (запасной ответ 1920×1080, 100 %), `ScalePercentOf`. `TrySetScale(device, percent)` — недокументированные `DisplayConfigGetDeviceInfo` (тип −3) / `SetDeviceInfo` (тип −4), ступени 100…500 %.

`Display/VirtualDisplaySetup.cs` — добавление разрешения: `AddModeElevatedAsync(w, h)` запускает свою копию `--vdd-add-mode ШxВ` через `runas`, ждёт до 2 мин. `Run(arg)`: коды 2 (плохой аргумент или вне 640..8192 × 480..8192), 3 (файл), 4 (устройство не перезапустилось), 0. `AddResolution(path, w, h)` дописывает `<resolution>` с `refresh_rate` 60 в `vdd_settings.xml` (запись через `.tmp`); путь — `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver\VDDPATH` или `C:\VirtualDisplayDriver`. `RestartDevice()` — SetupAPI, `DIF_PROPERTYCHANGE` для устройства с hardware id «MttVDD».

`Display/VirtualDisplaySetup.Install.cs` — установка: `FindPackage()` ищет папку `VirtualDrivers.Virtual-Display-Driver_*` в `%LOCALAPPDATA%\Microsoft\WinGet\Packages` и `%ProgramFiles%\WinGet\Packages` с `SignedDrivers\{x86|ARM64}\VDD\MttVDD.inf` и `Dependencies\devcon.exe` (папка «x86» — это x64-драйвер). `InstallElevatedAsync(package)` — копия `--vdd-install "<пакет>" "<отчёт>"` через `runas`, до 3 мин, −1 при отказе в UAC. `Install`: `PrepareSettings` (создаёт ключ `VDDPATH`, копирует приложенный `vdd_settings.xml`, существующий не трогает), `devcon find Root\MttVDD` → `update` или `install`; коды 0, 10 (пакет неполон), 11 (настройки), 20 (devcon). Аргументы разбирает `App.xaml.cs: OnStartup` до всего остального. Саму загрузку пакета winget'ом делает `MainViewModel.InstallVirtualDisplayDriverAsync`.

**Ограничения и запасные пути.** Нет драйвера: расширение отклоняется с причиной, парковка окон не делается, в возможностях `HasVirtualDisplay = false`. `TrySetScale` может не сработать на части сборок Windows — тогда остаётся масштаб Windows (только запись в журнал). Некоторые драйверы не перечисляют режимы, пока выход отключён — тогда пробуется размер экрана зрителя.

**Тесты.** `VirtualDisplayTests` (`PickMode`, `PlaceBeside`, `AddResolution`, `Run`). `Install`, `RestartDevice`, `TrySetScale`, `Attach/Detach` не покрыты.

### Маршрутизация канала Screen и UDP-датаграмм

**Как устроено.** `OnScreenFrameAsync` читает заголовок (`ScreenCodec.TryReadHeader`: тип, `StreamId`) и проверяет, что сессия совпадает с сессией вида/источника. К зрителю: `Frame`/`VideoFrame` → `viewer.OnFrameFromConnection`, `Still` → `viewer.OnStill`, `Cursor`/`CursorShape` → `viewer.OnPointerMessage` (мимо очереди кадров). К источнику: `Ack` → `OnAck`, `Pointer` → `OnPointer`, `Key` → `OnKey`, `Crop` → `OnCrop`, `Touch` → `OnTouch`, `Text` → `OnText`, `Window` → `OnWindowCommand`, `Size` → `OnViewerSize`, `UdpReady` → `OnUdpReady`, `Refresh` → `OnRefreshRequested`. `OnMediaPacket` (UDP): по заголовку `MediaCipher` виды `VideoLink.FrameKind = 2` и `ProbeKind = 3` идут зрителю, `ViewerReplies.AckKind = 4` и `ResendKind = 5` — источнику; открыть датаграмму может только ключ потока. `LoseMediaForTest` — внутренний крючок для тестов потерь.

**Тесты.** `VideoDatagramTests`, `ResendTests`, `PicturesGetThroughALinkThatLosesOneDatagramInTwelve` (Explicit).

### ScreenViewer: цикл декодирования и владение декодерами

**Что делает.** Объект вида, независимый от UI: принимает кадры, декодирует их в постоянную BGRA-картинку или в поверхность на видеокарте и отдаёт окну то, с чем оно успевает справиться.

**Как устроено.** Конструктор внутренний; сразу открывает UDP-путь по `info.MediaKey`, берёт `MediaTimerResolution.Hold()`, запускает `DecodeLoopAsync` и `SendLoopAsync` (очередь `_outbox` → канал Screen, по порядку). Декодирование начинается только после `Attach(present)`; до этого кадры копятся в неограниченной очереди `_frames`. Плитки (`DecodeTilesAsync`): `TileDecoder.DecodeInto` параллельно по плиткам, подтверждение, `Present(accumulates: true)`. Видео (`DecodeVideoAsync`): выбор формата и декодера (см. кодеки), `Expect(w, h)` (размер кадра меняется на ходу), `DecodeInto(data, ref _image, read: !hurry, onto: surface)`. Декодеры принадлежат циклу: освобождаются только в его `finally` (вместе с поверхностями); `DisposeAsync` ждёт цикл `DecoderFinishesWithin = 3 с` и при таймауте оставляет их — утечка лучше освобождения под работающим декодером (раньше это роняло процесс). Каждые 10 с (`ReportEvery`) в журнал пишется строка «View of …» со счётчиками.

**Ограничения и запасные пути.** Исключение ввода-вывода в цикле закрывает вид как потерю связи (`CanResume = true`). Битая плитка только журналируется.

**Тесты.** `MemoryTests`, `GpuDecodingTests`, `CatchUpDecodingTests` (в `LatencyTests.cs`), сквозные Explicit.

### Подтверждения, «поспешное» декодирование и передача кадра окну

**Что делает.** Источник шлёт столько, сколько зритель успевает подтверждать. Подтверждение уходит сразу после декодирования, не дожидаясь отрисовки: медленное окно теряет кадры, но не останавливает поток. Если зритель отстаёт, устаревшие кадры декодируются без вывода.

**Как устроено.** `AckAsync(frameNo, arrived)`: `held` — миллисекунды от прихода до этого момента, `painted` — длительность последней отрисовки (`FramePresented()` измеряет от выдачи кадра окну). Сначала датаграммой (`AckByDatagram`, если UDP работает), затем `ScreenCodec.EncodeAck` по соединению. Подтверждается и кадр, из которого картинка не вышла. Поспешность: `hurry` = уже был показан кадр И `_hurriedInARow < MaxHurried (2)` И размер не изменился И в очереди есть ещё кадры (`_waiting > 0`) — тогда кадр декодируется без чтения и не показывается; первый кадр и кадр со сменой размера не пропускаются никогда. `Present(...)`: если окно ещё держит прошлый буфер меньше `PresentGrace = 2 с` — кадр считается «не показан» (`_skipped`, `_missedFrames`), следующий показанный помечается изменённым целиком; иначе буферы `_image`/`_spare` меняются местами (для плиток содержимое копируется в запасной) и вызывается `present(FrameUpdate(FrameNo, Width, Height, Image, Dirty, Resized, CropId, Surface))`. Окно обязано вызвать `FramePresented()`.

**Сообщения.** `Ack = 2` (канал) и `AckKind = 4` (датаграмма, несёт до `AcksPerDatagram = 4` последних).

**Ограничения и запасные пути.** README и ARCHITECTURE §5.4/этап 5 всё ещё описывают «подтверждение после отрисовки» и окно в два кадра — в коде подтверждение после декодирования, а `ScreenSource.MaxInFlight = 3`.

**Тесты.** `CatchUpDecodingTests` (Theory), `LatencyTests`, `FlowControlTests` (сторона источника), `ResendTests`.

### Кадры на видеокарте: пул поверхностей и прямой показ

**Что делает.** Декодированный на видеокарте кадр не читается в память, а остаётся в общей текстуре, из которой окно рисует напрямую. Если окно так не может или перестало мочь, кадры снова приходят в памяти.

**Как устроено.** Окно выставляет `ScreenViewer.SurfacesWanted` (в `ScreenViewerWindow.Surfaces.cs: InitializeSurfaces`: настройка `DirectPresent`, аппаратный рендер WPF `RenderCapability.Tier >> 16 >= 2`, удалось создать мост Direct3D 9; при отказе показа окно ставит false). Кадр оставляется на карте, если не `hurry`, `SurfacesWanted` и `decoder.UsesGraphicsCard`. Два пула `SurfacePool`: `_wholeSurfaces` (кадры всего экрана) и `_partSurfaces` (кадры приближенной части; выбор по `_isPart[cropId]`, заполняется в `SetCrop`). В пуле три роли: `Shown` (окно рисует из неё), `Handed` (выдана, не подтверждена), `Spare` (в неё пишет декодер) — декодер никогда не пишет туда, откуда окно может рисовать. `SpareOf` создаёт поверхности по требованию (`decoder.CreateSurface`, не более 4 в наборе); при смене размера или декодера прежний набор уходит в `Retired` и освобождается только при следующей смене. `PresentSurface` выдаёт `FrameUpdate` с `Surface` и пустым `Image`; `FramePresented()` переводит `Handed → Shown`. Окно, не ответившее за `PresentGrace`, считается рисующим выданное.

**Настройки.** `DirectPresent` = true (читает окно).

**Ограничения и запасные пути.** Комментарий говорит «три поверхности», код допускает четвёртую. Отказ карты на H.264 → декодирование процессором, на HEVC → просьба отступить. На планшете (Intel Iris Xe) по ARCHITECTURE на момент 1.0.67 не проверено; запись о сеансе 1.0.68 говорит, что кадры оставались на карте.

**Тесты.** `SharedSurfaceTests`, `DirectPresentTests` (Explicit), `AWindowThatDrawsFromTheCardIsHandedPicturesThatStayOnIt` (Explicit).

### Приём кадров по UDP, ответы датаграммами и запросы досылки

**Что делает.** Кадры могут идти датаграммами, чтобы потерянный пакет не задерживал следующий кадр. Зритель собирает их, просит дослать недостающие куски и подтверждает кадры тем же путём.

**Как устроено.** `OpenMediaPath(key)`: ключ (base64, `MediaCipher.KeySize`) из `ScreenStreamMessage.MediaKey`; создаются `MediaCipher`, `VideoReassembly(OnWholePicture, OnLostPicture, null, askAgain)` и, если `Info.MediaReplies` и есть `sendMedia`, отдельный шифр ответов. `OnMediaPacket`: проба → ответ `UdpReady` не чаще `AnswerProbesEvery = 400 мс`; кадр → `ReportUdpWorks()` (один раз) и `reassembly.Add`. Целый кадр перекодируется в обычное сообщение `VideoFrame` и идёт в ту же очередь. Кадр, пришедший по соединению, сначала сообщает сборщику номер (`Passed`). `TickMediaAsync`: `reassembly.Tick()` каждые 4 мс, пока что-то в кусках, и каждые 50 мс в простое. Потерянный кадр (`OnLostPicture`): подтверждается (датаграммой и по соединению, `held = 0`) и запрашивается целый кадр `Refresh`. `AskAgain(frameNo, pieces)` → `ViewerReplies.WriteResend` (пустой список — весь кадр). Ответы запечатываются ключом потока с номерами от `ViewerSequenceBase = 2^63`.

**Сообщения.** `UdpReady = 13`, `Refresh = 14`; датаграммы видов 2, 3, 4, 5.

**Ограничения и запасные пути.** Нечитаемый ключ, источник без UDP или без `MediaReplies` — кадры остаются на соединении, досылка не запрашивается. На настоящем Wi‑Fi не проверено (ARCHITECTURE).

**Тесты.** `ResendTests`, `VideoDatagramTests`, `LatencyTests`, `PicturesGetThroughALinkThatLosesOneDatagramInTwelve` (Explicit).

### Часть экрана: обрезка, приближение, пересчёт позиций и размер показа

**Что делает.** Зритель может получать только часть картинки (выделенная рамкой область или видимая часть при приближении) и в том размере, в каком он её реально показывает. Позиции указателя и касаний при этом остаются в координатах целой картинки.

**Как устроено.** `ViewParts` (арифметика в долях 0..1): `For(visible)` — видимое плюс `Margin = 0.15` с каждой стороны; если площадь больше `WholeAbove = 0.85` — `Whole`. `StillServes(asked, visible, askedZoom, zoom)` — прежняя часть годится, пока масштаб тот же (±2 %), вид внутри неё и до края остаётся не меньше трети запаса. `Within(of, part)` — часть части. `ScreenViewer.SetCrop(crop, id = 0, part = null)`: запоминает `_part` (какой частью целого является запрос), `_isPart[id]`, шлёт `Crop` с именем `id`; имя возвращается в каждом видеокадре (`FrameUpdate.CropId`; источник объявляет `CropTags`). `SendPointer`/`SendTouch` переводят позицию в доли запрошенной части: `(x − part.X) / part.Width`; указатель источника переводится обратно. На источнике `OnCrop` нормализует обрезку и требует целый кадр; `TryMapToScreen`/`MapToScreen` переводят долю обрезки в физический пиксель монитора или видимых границ окна (с зажимом в границы). `SetShownSize(w, h)`: округление вверх до шага `SizeStep = 160`, зажим 320..7680, сообщение `Size` только при изменении; источник (`OnViewerSize`) зажимает 0..7680 × 0..4320 и по `FitOutput` кодирует в этом размере, если масштаб < 0,95, никогда не крупнее захваченного.

**Сообщения.** `Crop = 5`, `Size = 10`.

**Ограничения и запасные пути.** Для плиток и источника без `CropTags` приближение работает по-старому (без имён). Отрисовка части поверх целого в окне проверена только сборкой (ARCHITECTURE).

**Тесты.** `ViewPartTests`, `OutputSizeTests`, `PointerTests` (`TheWayToTheScreenAndBackAgree`), `ThePictureIsSentAtTheSizeTheViewerSaysItShowsIt`, `APartOfTheScreenComesBackUnderTheNameItWasAskedForBy` (оба Explicit).

### Чёткий кадр неподвижного экрана

**Что делает.** Когда экран источника замер, зритель получает один снимок с полным цветом и кладёт его поверх видео до следующего движения.

**Как устроено.** Источник (`ScreenSource.SendStillAsync`) шлёт `Still` после 600 мс неподвижности (только видео, не при сниженном битрейте). Зритель (`OnStill`): отбрасывает, если нет подписчиков `StillArrived` или `LastFrameNo > after`; декодирует `StillCodec.Decode` в `Task.Run`, повторно проверяет номер и размер, поднимает `StillArrived(viewer, StillPicture(Width, Height, Bgra, CropId, AfterFrameNo))`. Счётчик `StillsShown`.

**Настройки.** `SharpStill` = true (читает источник).

**Сообщения.** `Still = 15`.

**Тесты.** `StillCodecTests`, `AScreenThatStandsStillIsFollowedByASharpPictureOfIt` (Explicit).

### Указатель источника, рисуемый зрителем (DrawsPointer)

**Что делает.** Указатель мыши источника не врисовывается в кадр, а передаётся отдельно и рисуется зрителем — он движется с частотой мыши, а не кадров.

**Как устроено.** Источник включает это, только если пир объявил `DrawsPointer` (`PeerDrawsPointer`); тогда `WgcCapture.CaptureCursor = false` и работает `PointerLoopAsync` (каждые 8 мс, на экономии 16 мс): форма один раз (`CursorShape`, до 48 имён), позиция при сдвиге ≥ 0,5 пикселя кадра (`Cursor`). Зритель (`OnPointerMessage`): формы хранятся по `ShapeId` (до `MaxShapes = 64`, затем сброс), позиция переводится из долей части в доли целого; `Pointer`, событие `PointerMoved`, `PointerShape(id)`. `ShapeId = 0` — форму нарисовать не удалось, окно показывает свой указатель.

**Сообщения.** `Cursor = 11`, `CursorShape = 12`.

**Ограничения и запасные пути.** Пир без `DrawsPointer` получает указатель в кадре, как раньше. ARCHITECTURE §5.4 («курсор рисуется самим WGC») описывает только этот старый путь.

**Тесты.** `PointerTests`.

### Управление из вида: указатель, клавиши, касания, текст

**Что делает.** Зритель управляет источником мышью, клавиатурой, настоящими касаниями (до 10 пальцев) и вводом текста без клавиш. Источник исполняет это, только пока у него разрешено управление.

**Как устроено.** Зритель: `SendPointer(action, button, x, y, wheel)`, `SendKey(vk, scan, up, extended)`, `SendTouch(action 1 down / 2 move / 3 up / 4 cancel, contact 0–9, x, y)`, `SendText(text)` — всё через `_outbox`, по порядку. Источник (`ScreenSource`), каждый раз проверяя `AllowControl`: `OnPointer` — `InputInjector.MoveAbsolute`, кнопки, колёса (в т. ч. горизонтальное); на нажатии поток окна выводит окно вперёд, вызывается `RemoteInputTracker.NoteDown` и `ReportIfBlocked`. `OnKey` → `InputInjector.Key`. `OnText` → `InputInjector.TypeText` (Unicode-пакеты, перевод строки — Enter). `OnTouch` → `Injection/TouchInjector.Apply`. `TouchInjector`: `MaxContacts = 10` (id по модулю 10), каждый ввод несёт все активные контакты, покоящиеся пальцы переотправляются каждые `KeepAliveMs = 50` (таймер идёт только пока есть касания), `ReleaseAll()` снимает все пальцы с флагом отмены; ввод идёт через `IInjectionSink` (по умолчанию `InjectionRouter.Instance`, т. е. через повышенного помощника, когда он включён). Окно администратора впереди: `ReportIfBlocked` не чаще раза в 10 с шлёт зрителю `InputBlockedMessage(заголовок)` (обрабатывает `InputSharingService`).

Отпускание зажатого: при остановке потока `ScreenSource.DisposeAsync` вызывает `_injector.ReleaseAll()` (все удерживаемые клавиши и кнопки) и `_touch.Dispose()` (все пальцы). Со стороны зрителя окно само шлёт отпускания (`ScreenViewerWindow.ReleaseRemoteInput`: при потере фокуса, закрытии, по кнопке меню).

**Настройки.** `AllowControl` = true (читается на каждом событии, действует сразу).

**Сообщения.** `Pointer = 3`, `Key = 4`, `Touch = 6`, `Text = 7`; `InputBlockedMessage`.

**Ограничения и запасные пути.** `ControlAllowed` в описании потока не обновляется при смене настройки. `OnKey` вызывает `RemoteInputTracker.NoteKey()` до проверки `AllowControl`. `TryMapToScreen` возвращает false, пока не захвачен ни один кадр. Раскладка клавиатуры (`KeyboardLayoutMessage`) — в `InputSharingService`, не здесь.

**Тесты.** `TouchInjectorTests`, `PointerTests`; сквозного теста управления нет.

### RemoteInputTracker: «это нажатие пришло оттуда»

**Что делает.** Позволяет приложению отличить нажатие зрителя от местного: собственные оверлеи не открывают меню, которого зритель не видит, а опасные действия переспрашиваются.

**Как устроено.** Статический класс. `NoteDown(x, y)` — точка и время последнего нажатия зрителя; `NoteKey()`; `IsRemote(x, y)` — true в пределах `TolerancePixels = 12` и `FreshMs = 800`; `ActiveWithin(ms)`; событие `TouchEnded(peerId, streamId)` (через `NoteTouchEnded`, когда палец зрителя поднят). Потребители в приложении: `FloatingMenuWindow`, `ControlPanelWindow`, `PanelUi` (игнорируют такое нажатие), `RemoteGuard` (`ActiveWithin(4000)` → вопрос перед отключающим действием), `RemoteKeyboardWatcher` (см. ниже).

**Тесты.** `RemoteInputTrackerTests`.

### Подсказка экранной клавиатуры

**Что делает.** Касание текстового поля на источнике может поднять сенсорную клавиатуру у зрителя, касание мимо — убрать.

**Как устроено.** `RemoteKeyboardWatcher` (приложение) по `RemoteInputTracker.TouchEnded` через 250 мс смотрит через UI Automation, редактируем ли элемент в фокусе, и вызывает `SendKeyboardHintAsync(peerId, streamId, show)` — служба проверяет, что поток принадлежит этому пиру, и шлёт `ScreenKeyboardMessage`. У зрителя — `viewer.OnKeyboardHint` → событие `KeyboardHint`.

**Настройки.** `AutoKeyboardOnTap` = false (читает окно зрителя); `KeyboardButton` = true, `KeyboardButtonX` = 0.97, `KeyboardButtonY` = 0.62, `DictationButton` = true — тоже только окно.

**Сообщения.** `ScreenKeyboardMessage(StreamId, Show)`.

**Тесты.** Только кодек сообщения в `WinMirroring.Core.Tests`.

### Показатели, счётчики и отметка «подвисло»

**Что делает.** Зритель видит, как поток выглядит со стороны источника, и может одной кнопкой поставить в журналах обоих устройств метку в момент подвисания.

**Как устроено.** Источник раз в секунду шлёт `ScreenStatsMessage(AckMs, ViewerMs, EncoderMs, Kbps, Fps, Udp, SharePercent, GaveUp, Resent)`; у зрителя — `Stats`. Свои счётчики: `FramesDecoded`, `FramesNotShown`, `Counts` = `ViewCounts(Decoded, NotShown, Hurried, OnCard, ByDatagram, Rebuilt, GivenUp, AskedAgain)`. `MarkHitch(note)`: строка `HITCH marked here…` в свой журнал и `ScreenMarkMessage`; служба обрезает заметку до 200 символов, `ScreenSource.OnMark` пишет `HITCH marked by the viewer…` и сразу внеочередную строку потока.

**Настройки.** `ShowStats` = false (читает окно).

**Сообщения.** `ScreenStatsMessage`, `ScreenMarkMessage`.

**Тесты.** `TheViewerIsToldHowTheStreamIsDoingAndCanMarkAHitch` (Explicit).

### Миниатюры остальных мониторов

**Что делает.** Пока открыто меню, кнопки выбора монитора показывают маленькие живые картинки того, что на этих мониторах.

**Как устроено.** `ScreenViewer.WantMonitorPictures(on)` шлёт `ScreenThumbsMessage` при смене состояния или не чаще раза в 2 с; при выключении очищает `MonitorPictures`. Источник (`OnThumbsWanted`, только `Monitor`): продлевает желание на 6 с, цикл каждые 1,5 с снимает все мониторы, кроме показываемого (`MonitorThumbnail.Capture`), и шлёт `ScreenThumbMessage(StreamId, MonitorKey, Width, Height, Jpeg)`. Зритель (`OnThumbnail`): отбрасывает пустые и больше 256 КБ, ограничивает число разных мониторов (~16), событие `MonitorPicturesChanged`.

**Сообщения.** `ScreenThumbsMessage`, `ScreenThumbMessage`.

**Ограничения и запасные пути.** В меню на устройстве не проверено (ARCHITECTURE).

**Тесты.** Тот же Explicit-тест показателей.

### Программа на переднем плане

**Что делает.** Зритель знает, какая программа активна на источнике, и подставляет набор кнопок под неё.

**Как устроено.** `ScreenSource.ForegroundLoopAsync` раз в 400 мс берёт процесс активного окна (для потока окна — самого окна) и при смене имени шлёт `ScreenForegroundMessage`. Служба обрезает имя до 128 символов; `viewer.ForegroundProcess`, событие `ForegroundChanged`.

**Сообщения.** `ScreenForegroundMessage(StreamId, Process)`.

**Тесты.** Проверяется в том же Explicit-тесте (`ForegroundProcess`).

### Точка потока на экране источника (для файлов)

**Как устроено.** `ScreenStreamingService.TryMapToScreen(peerId, streamId, x, y, out point, bringForward = false)`: требует `AllowControl`, поток этого пира; координаты зажимаются в 0..1; при `bringForward` окно потока выводится вперёд. Использует `DragDropCoordinator.Viewer` (бросок файлов в точку вида и вытягивание из неё).

### Звук следует за зрителем

**Как устроено.** `RaiseSourcesChanged` поднимает `ViewersChanged` со списком peerId, смотрящих **мониторы** (`Kind == Monitor`; окна и второй монитор не считаются). Приложение подключает `screen.ViewersChanged += audio.SetViewers`; маршрут учитывает это при `AudioSettings.FollowScreenViewer` = true (раздел звука).

### Влияние экономии энергии

**Как устроено.** Зритель на экономии (`MirroringNode.Power.Saving`) просит в `ScreenRequestMessage.MaxFps` значение `max(1, PowerSettings.Fps)` (по умолчанию 30). Источник: `EffectiveMaxFps = min(Power.Fps(MaxFps), ViewerFps)`; на экономии сбор кадров из кодера раз в 8 мс вместо 2, указатель раз в 16 мс вместо 8. У зрителя независимо от режима: тик сборки 50 мс в простое; keepalive касаний только при касании.

**Ограничения и запасные пути.** `MaxFps` зрителя вычисляется один раз при запросе (переход на батарею позже не учитывается) и теряется при переключении монитора.

### Настройки ScreenSettings, которые читает этот код

| Свойство | По умолчанию | Где читается |
|---|---|---|
| `AllowViewing` | true | служба: запрос монитора, отправка окна, перетаскивание, расширение |
| `AllowControl` | true | служба: `TryMapToScreen`; `ScreenSource`: каждый ввод, `ControlAllowed` |
| `MaxFps` | 60 | `ScreenSource` (`StreamPacing`, `EffectiveMaxFps`) |
| `Quality` | 70 | `ScreenSource.BuildJpegMessages` |
| `VideoCodec` | `Auto` | служба: `NegotiateVideo`, `WithFormat` |
| `VideoBitrateKbps` | 12000 | `ScreenSource.ScaleBitrate` |
| `WindowDragEnabled` | true | служба: `OnFocusChanged`, `TrySendMovingWindowAsync` |
| `HideSentWindow` | true | служба: `ShouldHideDraggedWindow`, `SendWindowAsync` |
| `SharpStill` | true | `ScreenSource` |

Остальные (`CloseUnusedViewMinutes` 5, `AutoKeyboardOnTap` false, `KeyboardButton` true, `KeyboardButtonX` 0.97, `KeyboardButtonY` 0.62, `DirectPresent` true, `SwipeMonitors` true, `ShowStats` false, `DictationButton` true) в `WinMirroring.Screen` не читаются — только окно просмотра и `App`. Настройки меняются на месте в одном объекте (`MainViewModel.SaveScreen`), поэтому идущий поток видит новые значения; исключение — то, что зафиксировано при создании потока (кодек, отдельный указатель).

### Сводка сообщений

Управляющие (канал Control, JSON): `caps` DeviceCapabilitiesMessage (обе стороны); `screen-request` (зритель → источник); `screen-stream` (источник → зритель); `screen-stop` (любая); `screen-fallback` (зритель → источник); `screen-keyboard`, `screen-stats`, `screen-foreground`, `screen-thumb` (источник → зритель); `screen-thumbs`, `screen-mark` (зритель → источник); `display-invite` (хост → устройство); `display-extend` (устройство → хост); `display-result` (хост → устройство, только отказ); `input-blocked` (источник → зритель, обрабатывается в Input).

Канал Screen (`ScreenMessageType`): источник → зритель: `Frame 1`, `VideoFrame 9`, `Cursor 11`, `CursorShape 12`, `Still 15`; зритель → источник: `Ack 2`, `Pointer 3`, `Key 4`, `Crop 5`, `Touch 6`, `Text 7`, `Window 8`, `Size 10`, `UdpReady 13`, `Refresh 14`.

### Тесты: сводка по tests/WinMirroring.Screen.Tests

По этой области: `ScreenStreamIntegrationTests` (Explicit: `ViewerReceivesTheMonitor`, `ViewerReceivesTheMonitorAsVideo`, `ViewerReceivesTheMonitorAsHevc`, `ThePictureIsSentAtTheSizeTheViewerSaysItShowsIt`, `APartOfTheScreenComesBackUnderTheNameItWasAskedForBy`, `AViewThatLostItsSourceSaysItCanBeOpenedAgainAndCanBe`, `AWindowThatDrawsFromTheCardIsHandedPicturesThatStayOnIt`, `SwitchingMonitorsUnderAStreamDoesNotEndIt`, `PicturesGetThroughALinkThatLosesOneDatagramInTwelve`, `TheViewerIsToldHowTheStreamIsDoingAndCanMarkAHitch`, `AScreenThatStandsStillIsFollowedByASharpPictureOfIt`; обычный: `ExtendWithoutTheDriverIsRefusedWithAReason`), `VirtualDisplayTests`, `WindowPlacerTests`, `MonitorChoiceTests`, `MonitorNeighbourTests`, `ViewEndingTests`, `ViewPartTests`, `RemoteInputTrackerTests`, `TouchInjectorTests`, `PointerTests`, `OutputSizeTests`, `LatencyTests` и `CatchUpDecodingTests`, `ResendTests`, `SharedSurfaceTests`, `DirectPresentTests` (единственный метод Explicit), `MemoryTests`. Смежные (чужой раздел): `FlowControlTests`, `VideoDatagramTests`, `TileAndCodecTests`, `VideoCodecTests`, `HevcTests`, `GpuDecodingTests` (2 Explicit), `GpuEncodingTests` (1 Explicit), `EncoderPipelineTests` (1 Explicit), `StillCodecTests`, `BgraScalerTests` (1 Explicit), `Nv12ConverterTests`, `MfOutputSampleTests`; пробы целиком Explicit: `ConversionCostProbe`, `DisplayColourProbe`, `FrameSizeProbe`.

Не покрыто тестами: отправка и перетаскивание окна, парковка и возврат окон, `WindowDragWatcher`, установка драйвера и `TrySetScale`, обмен возможностями как таковой, отступления кодека по жалобе зрителя, управление из вида насквозь, подсказка клавиатуры.

### Расхождения с документацией и подозрительные места (по чтению кода, не запускалось)

- ARCHITECTURE §5.4: `ScreenCapabilityMessage` не существует — в коде `DeviceCapabilitiesMessage`; «кадр подтверждается только после отрисовки» и «не больше двух неподтверждённых» устарели (подтверждение после декодирования, `MaxInFlight = 3`); «курсор рисуется самим WGC» верно только для пиров без `DrawsPointer`; список `VideoCodec` там без `Hevc`.
- README: «по умолчанию не больше 30 кадров/с» — в коде 60; «здесь окно вернётся на место» — при `HideSentWindow` (по умолчанию включено) и драйвере окно паркуется на виртуальном мониторе.
- Зритель после жалобы на HEVC при отсутствующем декодере не создаёт H.264-декодер и не может пожаловаться второй раз (см. раздел о кодеках).
- `ScreenViewer.DisposeAsync` не вызывается для обычно закрытого вида: удержание таймера 1 мс и шифры не освобождаются.
- Переключение монитора сбрасывает `ViewerFps`.
- Выключение `AllowViewing` не останавливает идущие потоки; отказ на переключение монитора оставляет источник работать без зрителя.
- Парковка перетащенного окна не зависит от того, запустился ли поток.
- Приглашение стать вторым монитором и входящее окно принимаются без подтверждения.
