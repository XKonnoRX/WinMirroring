# Ядро (WinMirroring.Core)

> Часть справочника [docs/FEATURES.md](../FEATURES.md). Составлено по коду версии 1.0.75; источник правды — код.

Библиотека `src/WinMirroring.Core` (около 6 100 строк, единственная зависимость — `Microsoft.Extensions.Logging.Abstractions`). Содержит всё, что одинаково для приложения и `wm-cli`: идентичность устройства, настройки, выбор сети, обнаружение, TLS-транспорт, рукопожатие, сопряжение, сессии, UDP-сокет для медиа, энергорежим. Только IPv4. Внутренности открыты тестам через `InternalsVisibleTo("WinMirroring.Core.Tests")`.

Тесты ядра лежат в `tests/WinMirroring.Core.Tests`; тестов с `[Fact(Explicit = true)]` в этом проекте нет (все такие — в проектах Audio/Clipboard/Input/Screen).

---

### Узел: `MirroringNode` и `NodeOptions`

**Что делает.** Один объект на устройство, который собирает всё ядро: загружает настройки, создаёт или находит сертификат, поднимает слушатель TCP, обнаружение и UDP-сокет медиа, держит сессии. Приложение и CLI отличаются только реализацией `IPairingUserInterface`, которую передают в конструктор.

**Как устроено.** `src/WinMirroring.Core/MirroringNode.cs`.
- `NodeOptions`: `DataDirectory` (по умолчанию `SettingsStore.DefaultDataDirectory` = `%LocalAppData%\WinMirroring`), `IncludeLoopback` (привязка и к loopback — тесты, CLI), `DeviceName`, `DiscoveryPort`, `TransportPort`, `ManualAddresses`. Непустые значения опций записываются в настройки и сохраняются (ручные адреса добавляются, если их ещё нет).
- Конструктор: `SettingsStore.Load()` (если файл настроек не прочитался — запись уровня Error в журнал с причиной и именем, под которым файл сохранён, см. «Хранилище настроек») → `InterfacePin.Enabled = Settings.PinConnectionsToInterface` → `DeviceIdentityStore.LoadOrCreate` → `PowerMode` → `PeerDirectory` (сопряжённые устройства сразу заносятся как `Offline`) → `PeerSessionManager` → `PeerIntroductions.Start()` → `NetworkSelection.Resolve`.
- Наружу: `SettingsStore`, `Settings`, `Identity`, `Power`, `Peers`, `Sessions`, `Introductions`, `Network`, `IsRunning`, `ListeningEndPoints`, `MediaPort` (0, если UDP-сокет не поднялся), событие `MediaPacketReceived`, методы `SendMedia`, `PrioritizeMedia`/`ReleaseMediaPriority`, `SaveSettings`, `Rename`, `StartAsync`, `RestartNetworkAsync`, `StopAsync`, `PairAsync`, `UnpairAsync`, `IsSourceAllowed`, `DisposeAsync` (повторный вызов безвреден).
- `StartNetwork()` создаёт `PeerListener` (TCP, `Settings.TransportPort`), `DiscoveryService` (UDP, `Settings.DiscoveryPort`) и `MediaSocket` (UDP на номере транспортного порта). `StopAsync` дополнительно закрывает все сессии с причиной `shutdown`; `RestartNetworkAsync` сессии не трогает и заодно переносит `Settings.DeviceName` в `Identity.Name`.
- Все операции с сетью сериализованы `SemaphoreSlim _networkGate`.

**Ограничения и запасные пути.** Если UDP-порт медиа занят, узел продолжает работать без него (предупреждение в журнале, `SendMedia` возвращает false). Если не выбран ни один пригодный интерфейс, узел «простаивает» (только предупреждение). Комментарий класса «The whole stage-1 node» устарел: узел давно обслуживает все функции.

**Тесты.** `NodeIntegrationTests` (`DisposingTwiceIsHarmless`, `ARenameIsKeptAndIsWhatTheDeviceIntroducesItselfBy` и остальные сквозные).

### Переименование устройства

**Что делает.** `MirroringNode.Rename(name)` меняет имя сразу в двух местах: в настройках на диске и в `Identity.Name`, которым устройство представляется в рукопожатии и в каждом маяке. Пустое имя заменяется на `Environment.MachineName`. Ничего не перепривязывается: маяки строятся из `Identity` каждый раз заново.

**Тесты.** `NodeIntegrationTests.ARenameIsKeptAndIsWhatTheDeviceIntroducesItselfBy`.

### Идентичность устройства и сертификат

**Что делает.** У устройства есть постоянный id, отображаемое имя и самоподписанный сертификат. Отпечаток сертификата — и есть личность устройства в сети: его закрепляют при сопряжении и сверяют при каждом соединении.

**Как устроено.**
- `src/WinMirroring.Core/Identity/DeviceIdentityStore.cs`: `LoadOrCreate` создаёт `DeviceId` (`Guid.NewGuid().ToString("N")`), имя (`Environment.MachineName`) и сертификат, если их нет; возвращает true, когда настройки изменились. Сертификат: ECDSA P-256, `CN=WinMirroring {deviceId}`, SAN `winmirroring` (`ProtocolConstants.TlsTargetHost`), EKU serverAuth + clientAuth, срок от «вчера» до +20 лет. Ключ кладётся в `CurrentUser\My` (`PersistKeySet | UserKeySet | Exportable`), потому что SChannel не принимает эфемерные ключи для TLS-сервера; в настройках хранится только `CertificateThumbprint`. Есть `FindInStore`, `CreateAndStore`, `RemoveFromStore` (последним пользуются тесты).
- `src/WinMirroring.Core/Identity/DeviceIdentity.cs`: `Id`, `Name` (изменяемое), `Certificate`, `Fingerprint`; `Sign` (ECDSA + SHA-256) и статический `Verify`.
- `src/WinMirroring.Core/Identity/CertificateFingerprint.cs`: отпечаток = SHA-256 от DER, hex в верхнем регистре; `AreEqual` без учёта регистра (пустая строка никогда не равна); `ToDisplay` — первые 16 hex-цифр группами по четыре.

**Настройки.** `AppSettings.DeviceId`, `DeviceName`, `CertificateThumbprint`.

**Ограничения и запасные пути.** Если сертификата с сохранённым отпечатком нет в хранилище или у него нет закрытого ключа, молча создаётся новый — устройство становится «другим», и все сопряжения на той стороне перестают сходиться по отпечатку.

**Тесты.** Отдельных нет; покрыто косвенно `NodeIntegrationTests` (каждый узел создаёт и удаляет сертификат).

### Хранилище настроек и защита каталога данных

**Что делает.** Настройки устройства лежат в одном файле `settings.json` в каталоге данных и сохраняются атомарно. Каталог данных — это и есть устройство (id, сертификат, список сопряжённых), поэтому ядро умеет сказать, занят ли каталог другим узлом в другом процессе.

**Как устроено.** `src/WinMirroring.Core/Settings/SettingsStore.cs`.
- JSON: camelCase, с отступами, комментарии и висячие запятые при чтении допускаются, null пишется (`DefaultIgnoreCondition.Never`). Конвертеров перечислений нет, поэтому `PowerSaving` и `VideoCodec` хранятся **числами**.
- `Save`: запись в `settings.json.tmp`, затем `File.Move(overwrite: true)`, под `lock`.
- `Load`: нет файла — новые `AppSettings`. Файл, который не читается как настройки (`JsonException`: обрезан, пуст, забит нулями; либо JSON-литерал `null`), **переносится** в `settings.json.corrupt-ГГГГММДД-ЧЧммсс` (при совпадении имени добавляется `-2`, `-3`…; если перенести нельзя — копируется под этим именем), возвращаются настройки по умолчанию. Что произошло, говорит свойство `Unreadable` (`UnreadableSettings(KeptAs, Reason)`; `KeptAs == null` — файл не удалось ни перенести, ни скопировать); при успешном чтении и при отсутствии файла оно `null`. Своего журнала у хранилища нет: запись уровня Error делает `MirroringNode`.
- Занятость: именованный мьютекс `Local\WinMirroring-data-<первые 32 hex SHA-256 от полного пути в нижнем регистре>`. Экземпляр держит его всё время жизни (`SharedWithAnotherNode` = не удалось взять); статический `IsInUseByAnotherNode(dir)` берёт и сразу отпускает. `AbandonedMutexException` считается «свободно, теперь наше»; любая другая ошибка — «свободно».

**Ограничения и запасные пути.**
- Проверка рекомендательная: `SettingsStore` работает и в занятом каталоге. Отказывается работать только `wm-cli` (`tools/WinMirroring.Cli/Program.cs`, код возврата `Exec.DirectoryInUse`), у которого каталог по умолчанию — `<DefaultDataDirectory>\cli`.
- Мьютекс принадлежит потоку, который его взял: вопрос с того же потока даёт «свободно»; `ReleaseMutex` из `Dispose` на другом потоке бросает исключение, которое глотается, после чего дескриптор просто закрывается. Комментарий в коде требует создавать узел на потоке, живущем столько же, сколько узел.
- `SharedWithAnotherNode` в `src/` и `tools/` никем не читается.
- Испорченный `settings.json` по-прежнему означает новое устройство: новый id и новый сертификат, сопряжений нет. Но старый файл не затирается: он лежит рядом под именем с отметкой времени, прежний сертификат остаётся в `CurrentUser\My`, и после починки файла вручную и возврата его на место устройство снова прежнее. Автоматического восстановления нет, уведомления пользователю тоже — только запись в журнале.
- `Save` не сбрасывает данные на диск перед переименованием (`File.WriteAllText` + `File.Move`): при отключении питания сразу после сохранения целостность файла не гарантирована.
- `AppSettings` не потокобезопасен: `PairedPeers` меняется из потоков сессий, а `Save` защищает замком только запись файла.

**Тесты.** `DataDirectoryTests` (`ADirectoryAnotherNodeHasIsSaidToBeInUseUntilThatNodeLetsGo`, `HoldingOneDirectorySaysNothingAboutAnother`, `ASharedDirectoryStillLoadsAndSaves`), `UnreadableSettingsTests` (`AFileThatCannotBeReadIsMovedOutOfTheWayAndNothingIsWrittenOverIt`, `ASecondUnreadableFileDoesNotTakeThePlaceOfTheFirst`, `WhateverIsNotSettingsIsKeptAllTheSame`, `SettingsThatCanBeReadAndNoSettingsAtAllAreNeitherOfThemUnreadable`), `NodeIntegrationTests.ANodeWhoseSettingsCannotBeReadSaysSoAndKeepsTheFile`, `ButtonBarSettingsTests` (загрузка/сохранение).

### Выбор сетевых интерфейсов

**Что делает.** Пользователь выбирает, на каких адаптерах устройство слушает и объявляет себя; пустой выбор означает «все поднятые». По выбранным подсетям фильтруются входящие маяки и соединения.

**Как устроено.**
- `src/WinMirroring.Core/Network/NetworkInterfaceCatalog.cs`: `Enumerate(includeLoopback)` — адаптеры в состоянии `Up`, по одной записи на каждый IPv4-адрес, кроме 169.254.x.x; loopback только по запросу. `IsVpnLike` = тип `Tunnel`/`Ppp` либо имя/описание подходит под `wireguard|tailscale|zerotier|openvpn|hamachi|radmin|\btap\b|\btun\b|vpn` (используется интерфейсом приложения для пометки «VPN/туннель»).
- `src/WinMirroring.Core/Network/NetworkInterfaceInfo.cs`: `Id, Name, Description, Address, Subnet, Type, IsLoopback, IsVpnLike`.
- `src/WinMirroring.Core/Network/NetworkSelection.cs`: `Resolve(selectedIds, includeLoopback)` → `AllInterfaces`, `SelectedInterfaces`, `BindAddresses`; `IsInSelectedSubnet(address)` (loopback проходит, если включён).
- `src/WinMirroring.Core/Network/IPv4Subnet.cs`: CIDR-арифметика (`Network`, `Broadcast`, `Contains`, `PrefixFromMask`), `IsPointToPoint` для /31 и /32 (у VPN-туннелей нет broadcast).

**Настройки.** `AppSettings.SelectedInterfaceIds` (по умолчанию пусто = все).

**Тесты.** `IPv4SubnetTests` (`ComputesNetworkAndBroadcast`, `ContainsOnlyAddressesInsideTheNetwork`, `PointToPointHasNoBroadcast`, `PrefixFromMask`).

### Слежение за сменой сети

**Что делает.** Когда у машины меняются адреса (сон, другая сеть, кабель вместо Wi-Fi), слушатель и обнаружение перезапускаются сами, без события от Windows. Существующие сессии при этом не закрываются.

**Как устроено.** `MirroringNode.CheckNetworkAsync` по таймеру: каждые 3 с (`LookAtTheNetwork`), в режиме экономии — 10 с (`LookAtTheNetworkOnBattery`). Сравнивается «подпись» сети: отсортированный список `"{Name} {Address}"` выбранных интерфейсов. При отличии — `RestartNetworkAsync`, счётчик `NetworkRestarts`. Таймер перевооружается после каждой проверки с интервалом по текущему энергорежиму. Для тестов подменяется `internal Func<NetworkSelection> ResolveNetwork`.

**Ограничения и запасные пути.** Опрос сделан намеренно вместо события `NetworkAddressChanged`: по комментарию, прежняя схема с отложенным перезапуском никогда не срабатывала (ноутбук пять часов объявлял себя в покинутой сети).

**Тесты.** `NodeIntegrationTests.ANodeNoticesByItselfThatTheMachineMovedToAnotherNetwork`.

### Обнаружение: маяки в локальной сети

**Что делает.** Устройства находят друг друга без настройки: каждое раз в пару секунд объявляет себя на всех выбранных подсетях и слушает чужие объявления. Найденное устройство появляется в списке; с сопряжённым сразу устанавливается соединение.

**Как устроено.** `src/WinMirroring.Core/Discovery/DiscoveryService.cs`, `Beacon.cs`, `DiscoveredPeer.cs`.
- Датаграмма: `WMIR` (4 байта) + версия протокола (1 байт) + JSON camelCase `{id, name, transportPort, discoveryPort, fingerprint, isProbe}`. `Beacon.TryDecode` отбрасывает чужую магию, **другую версию протокола**, пустые id/отпечаток и порты вне 1..65535.
- Приём: один сокет на `0.0.0.0:DiscoveryPort` (`ReuseAddress`, broadcast, `SIO_UDP_CONNRESET` выключен), вступает в multicast-группу `239.255.47.62` на каждом выбранном не-loopback интерфейсе.
- Отправка: по сокету на интерфейс (привязан к его адресу, multicast TTL 1), кроме подсетей /31–/32. Каждый цикл — направленный broadcast подсети и multicast. Период `BeaconInterval` 2 с, в режиме экономии `BeaconOnBattery` 4 с.
- Принятый маяк: свой id игнорируется; источник должен быть в выбранной подсети или среди адресов, которые мы сами зондировали (`IsSourceAllowed`); затем событие `PeerSeen(DiscoveredPeer)` с адресами `remote:TransportPort` и `remote:DiscoveryPort`.
- Неудачные отправки учитываются по паре (интерфейс, адресат): первая ошибка пишется в журнал, повторы суммируются раз в 10 минут (`FailureReportInterval`), восстановление тоже пишется.

**Настройки.** `AppSettings.DiscoveryPort` (47620), `TransportPort` (47621), `SelectedInterfaceIds`.

**Ограничения и запасные пути.**
- Устройства с разной версией протокола не видят маяков друг друга вообще.
- На loopback нет ни broadcast, ни multicast — узлы на одной машине находятся только зондами по ручным адресам, и порты обнаружения у них должны различаться.
- Сокет приёма привязан к `0.0.0.0`, а не к выбранным адресам; отбор идёт по адресу источника.
- Устройство без сессии, от которого `ProtocolConstants.PeerExpiry` (8 с) не было маяков, переходит из `Discovered` в `Offline` («не в сети»): каталог просматривается раз в 2 с (см. «Каталог устройств»). Из списка оно при этом не удаляется — ни сопряжённое, ни чужое; чужие устройства, однажды увиденные в сети, остаются в каталоге как `Offline` до конца процесса.
- Маяк не аутентифицирован: отпечаток из него лишь подсказка, проверка идёт в TLS и рукопожатии.
- В классе остался неиспользуемый метод `WaitNextTickAsync`.

**Тесты.** `CodecTests.BeaconRoundTrips`, `CodecTests.BeaconRejectsGarbage`; сквозное обнаружение — `NodeIntegrationTests.TwoNodesDiscoverPairAndReconnect`.

### Обнаружение через VPN: ручные и запомненные адреса

**Что делает.** Там, где broadcast не проходит (VPN), устройство зондирует конкретные адреса: введённые пользователем и те, по которым сопряжённые устройства были достижимы раньше. Зонд — это маяк с просьбой ответить своим маяком.

**Как устроено.**
- `src/WinMirroring.Core/Discovery/ManualAddress.cs`: формат `host` или `host:discoveryPort`; хост без пробелов.
- Каждый цикл объявления `DiscoveryService.CollectProbeTargetsAsync` собирает цели: ручные адреса (имена резолвятся только в IPv4, тайм-аут 3 с, кэш 60 с при успехе и 15 с при неудаче) плюс `PeerSessionManager.RememberedDiscoveryEndPoints()`. Каждой цели уходит маяк с `isProbe = true` с сокета приёма; адрес запоминается в `_probedAddresses`, после чего ответы с него принимаются и вне выбранных подсетей.
- Получатель зонда отвечает обычным маяком unicast на `адрес отправителя : discoveryPort из маяка`.
- Запомненные адреса: `PairedPeer.LastKnownAddresses` (до 5, новые первыми, `RememberAddress`), записываются при сопряжении и каждый раз, когда сессия становится `Ready`. Зондируются только пока с устройством нет сессии.

**Настройки.** `AppSettings.ManualPeerAddresses` (пусто), `PairedPeer.LastKnownAddresses`, `PairedPeer.DiscoveryPort`.

**Ограничения и запасные пути.** `_probedAddresses` только растёт до перезапуска сети. Адрес не из выбранных подсетей допускается к TCP-слушателю, если он зондировался или запомнен (`MirroringNode.IsSourceAllowed`).

**Тесты.** `PairingCryptoTests.ManualAddressParses`, `PairingCryptoTests.ManualAddressRejectsInvalid`; все `NodeIntegrationTests` работают через ручные адреса на loopback.

### Сопряжённые устройства в сети Tailscale

**Что делает.** Если на машине стоит Tailscale, сопряжённые устройства ищутся и в его сети — по имени, без ввода адреса. Зонды уходят только машинам, которые называются как уже сопряжённое устройство; остальные машины сети не трогаются.

**Как устроено.** `src/WinMirroring.Core/Network/TailscalePeers.cs` (статический, общий на процесс). `AddressesOf(names)` никогда не ждёт: отдаёт прошлое чтение и, если ему больше 30 с (`KeptFor`), запускает новое в фоне — `%ProgramFiles%\Tailscale\tailscale.exe status --json`, ожидание до 3 с (`AnswerWithin`), затем процесс убивается. `Parse` берёт из объекта `Peer` поля `HostName`, первый IPv4 из `TailscaleIPs`, `Online`. `Pick` — машины в сети с именем из списка, без учёта регистра. Вызывается из `PeerSessionManager.RememberedDiscoveryEndPoints` с `PairedPeer.Name`, порт — `PairedPeer.DiscoveryPort`.

**Настройки.** `AppSettings.FindPeersOnTailscale` (true).

**Ограничения и запасные пути.** Сравнивается имя машины в Tailscale с именем устройства в WinMirroring: совпадают, пока устройство не переименовано (по умолчанию имя — `Environment.MachineName`). Первое обращение после старта всегда пустое. Нет Tailscale — пустой список. `InterfacePin` такие соединения не привязывает (/32). По `docs/ARCHITECTURE.md` между реальными устройствами не проверено.

**Тесты.** `TailscalePeerTests` (`TheListIsReadAsTailscaleWritesIt`, `OnlyAMachineWithAPairedDevicesNameIsEverPicked`, `AMachineThatIsOfflineIsNotAsked`, `WhatIsNotAListIsNoMachines`).

### Транспорт: TCP + TLS (`PeerListener`, `PeerConnection`)

**Что делает.** Между двумя устройствами одно TCP-соединение, защищённое TLS, по которому идут все каналы. Сервер предъявляет сертификат устройства; клиент доказывает себя уже внутри туннеля.

**Как устроено.**
- `src/WinMirroring.Core/Transport/PeerListener.cs`: по `TcpListener` на каждый выбранный адрес (`NetworkSelection.BindAddresses`) на `TransportPort`, backlog 16. Соединение с адреса, не прошедшего `MirroringNode.IsSourceAllowed` (выбранная подсеть / зондированный / запомненный адрес), закрывается сразу. Остальные уходят в `PeerSessionManager.HandleInboundAsync`.
- `src/WinMirroring.Core/Transport/PeerConnection.cs`:
  - `ConnectAsync`: тайм-аут соединения `ConnectTimeout` 5 с; TLS-клиент с SNI `winmirroring`, без проверки цепочки и отзыва — сертификат принимает только переданный колбэк (сравнение отпечатка); тайм-аут TLS `HandshakeTimeout` 15 с.
  - `AcceptAsync`: TLS-сервер с сертификатом устройства, клиентский сертификат не запрашивается, тайм-аут 15 с.
  - `EnabledSslProtocols = SslProtocols.None` (версию выбирает ОС), `EncryptionPolicy.RequireEncryption`; согласованная версия пишется в журнал (`NegotiatedProtocol`).
  - Сокет: `NoDelay`; keepalive: 5 с простоя, пробы раз в 1 с, 5 попыток; `TCP_MAXRT` = 10 с.
  - Свойства: `IsInitiator`, `PinnedTo`, `RemoteCertificate` (только у инициатора), `RemoteEndPoint`, `LocalEndPoint`.

**Настройки.** `AppSettings.TransportPort` (47621), `PinConnectionsToInterface`.

**Ограничения и запасные пути.** Ошибки установки сокетных опций глотаются. TLS 1.3 не зафиксирован кодом.

**Тесты.** Косвенно `NodeIntegrationTests`; `InterfacePinTests.ASocketHeldToAnInterfaceStillConnects`.

### Кадры, части сообщений и каналы

**Что делает.** Все данные сессии идут кадрами с номером логического канала. Большое сообщение режется на части, чтобы не задерживать нажатие клавиши, подтверждение кадра или пульс.

**Как устроено.** `src/WinMirroring.Core/Transport/FrameCodec.cs`, `ChannelId.cs`.
- Кадр: 4 байта длины (big-endian) + 1 байт канала + данные. `HeaderSize` = 5, предел `ProtocolConstants.MaxFrameSize` = 16 МБ.
- Два старших бита длины: `0x8000_0000` — «часть сообщения», `0x4000_0000` — «последняя часть». Данные части начинаются с 2-байтового id сообщения.
- `PeerConnection.SendAsync`: до `MaxUnsplitPayload` = 32 КБ — один кадр; больше — части по 32 КБ, замок записи (`SemaphoreSlim`) отпускается между частями, после каждой `Flush`. Сообщение длиннее `MaxFrameSize` (16 МБ) не отправляется вовсе: `MessageTooLargeException` (`Transport/MessageTooLargeException.cs`, наследник `InvalidOperationException`, свойства `Channel` и `Length`) бросается до записи первой части. Ровно 16 МБ проходит.
- `FrameCodec.Reader` (один на соединение) собирает части по ключу (канал, id); не больше 32 незавершённых сообщений, собранное сообщение не больше 16 МБ, иначе `InvalidDataException` (сессия закрывается). Чистый конец потока до заголовка даёт `null`.
- Каналы `ChannelId`: `Control`=0, `Input`=1, `Clipboard`=2, `File`=3, `Audio`=4, `Screen`=5, `Shell`=6.

**Ограничения и запасные пути.** Слишком длинное сообщение отвергает отправитель; получатель по-прежнему рвёт сессию, если части сложились больше чем в 16 МБ (так поступит устройство любой версии с отправителем старой версии, который предел не проверяет). `MessageTooLargeException` — это `InvalidOperationException`, потому что именно его все вызывающие `PeerSession.SendAsync` уже ловят как «не отправлено» (закрытая сессия, канал ещё не открыт); сообщение при этом просто пропадает, а что с этим делать, решает вызывающий (буфер обмена проверяет предел сам и сообщает пользователю; поток экрана завершится с причиной `connection: …`). `ChannelId.Audio` объявлен, но нигде в `src/` и `tools/` не используется — звук идёт по UDP.

**Тесты.** `CodecTests.FrameRoundTripsAndEndsCleanly`, `FrameRejectsOversizedLength`, `MessagesInPartsSurviveBeingInterleaved`; `NodeIntegrationTests.AMessageTooLongForTheOtherSideIsRefusedBeforeItIsSentAndTheSessionLives`.

### Привязка соединения к интерфейсу (`InterfacePin`)

**Что делает.** Соединение с устройством из собственной подсети «держится» за тот интерфейс, на котором это устройство находится, чтобы включение или выключение VPN (смена таблицы маршрутов) не сбрасывало сессию.

**Как устроено.** `src/WinMirroring.Core/Transport/InterfacePin.cs`. `Choose(candidates, remote)` выбирает интерфейс, чья подсеть содержит адрес пира; при нескольких — самую узкую; /32, loopback и IPv6 не рассматриваются. `TryPin(socket, remote, bind)`: для исходящего сокета сначала `Bind` к адресу интерфейса, затем опция `IP_UNICAST_IF` (31) с индексом интерфейса в сетевом порядке байт. Применяется и к исходящим, и к принятым соединениям; результат — в `PeerConnection.PinnedTo` и в журнале.

**Настройки.** `AppSettings.PinConnectionsToInterface` (true) → статическое `InterfacePin.Enabled`, читается один раз в конструкторе `MirroringNode`.

**Ограничения и запасные пути.** Если привязанный сокет не соединился, сразу пробуется обычный (без привязки). Пир вне своих подсетей не привязывается. Флаг статический на процесс, смена настройки требует перезапуска. Комментарий в коде: это попытка — VPN, который сам рвёт существующие соединения, привязка не остановит. UDP (медиа, обнаружение) не привязан.

**Тесты.** `InterfacePinTests` (`APeerOnTheSameSubnetIsHeldToThatInterface`, `TheNarrowerSubnetWinsWhenTwoContainThePeer`, `APeerOnNoneOfThisMachinesSubnetsIsNotHeldToAnything`, `AnIpv6PeerIsLeftAlone`, `ASocketHeldToAnInterfaceStillConnects`, `SwitchedOffNothingIsHeld`).

### Рукопожатие (`PeerHandshake`)

**Что делает.** Сразу после TLS стороны представляются друг другу, клиент доказывает владение своим ключом, и обе стороны узнают, сопряжены ли они взаимно.

**Как устроено.** `src/WinMirroring.Core/Peers/PeerHandshake.cs`, общий тайм-аут `HandshakeTimeout` 15 с, всё в канале `Control`.
1. Инициатор → `hello` (id, имя, версия, сертификат в base64, порты; без вызова).
2. Ответчик проверяет версию и читаемость сертификата, шлёт свой `hello` с вызовом (32 случайных байта).
3. Инициатор проверяет версию, наличие вызова и то, что сертификат из `hello` совпадает с TLS-сертификатом; подписывает `"WinMirroring-auth-v1" ‖ вызов ‖ отпечаток сервера ‖ отпечаток клиента` и шлёт `auth` (подпись, увиденный отпечаток сервера, «я вас закрепил»).
4. Ответчик сверяет увиденный клиентом отпечаток со своим (так обнаруживается посредник в TLS), проверяет подпись и шлёт `auth-result(ok, paired)`.
5. `HandshakeResult.PairedOnBothSides` = «да» с обеих сторон; «сопряжён» значит запись в `PairedPeers` с тем же отпечатком **и** тем же id (`PeerSessionManager.IsPaired`). `HandshakeResult.PairedHere` — собственный ответ этой стороны, каким он был в момент рукопожатия (в сессии — `PeerSession.SaidPairedInHandshake`); по проводу ничего нового не передаётся.

Причины отказа в `auth-result.reason`: `unsupported-version`, `bad-certificate`, `server-fingerprint-mismatch`, `bad-signature`. Ошибки — `HandshakeException`.

**Сообщения.** `hello`, `auth`, `auth-result`.

**Ограничения и запасные пути.** Версия протокола сравнивается строго (сейчас 4); совместимость между версиями обеспечивается только необязательными полями сообщений.

**Тесты.** Отдельных нет; покрыто `NodeIntegrationTests`.

### Сессия (`PeerSession`): состояния, каналы, управляющие сообщения

**Что делает.** Одна аутентифицированная связь с устройством. Пока устройства не сопряжены взаимно, по ней ходят только управляющие сообщения и сопряжение; после — открываются каналы функций.

**Как устроено.** `src/WinMirroring.Core/Peers/PeerSession.cs`.
- Состояния `SessionState`: `Unpaired`, `Pairing`, `Ready`, `Closed`. Начальное — `Ready` или `Unpaired` по итогу рукопожатия. События `StateChanged`, `Closed(session, error)`, `WaitForCloseAsync()`.
- Свойства: `PeerId`, `PeerName`, `PeerFingerprint`, `PeerCertificate`, порты пира, `IsInitiator`, `InitiatorId`, `RemoteEndPoint`, `PeerTransportEndPoint`/`PeerDiscoveryEndPoint` (адрес соединения + порт из `hello`), `LastRoundTrip`, `LastReceivedTicks`, `ConfirmationCode`, `SaidPairedInHandshake` (что эта сторона ответила в рукопожатии на «сопряжены ли вы»).
- Отправка: `SendAsync(channel, payload)` — любой канал, кроме `Control`, требует `Ready` (иначе `InvalidOperationException`); сообщение больше 16 МБ — `MessageTooLargeException`, сессия остаётся; `SendControlAsync(message)`.
- Приём: `RegisterHandler(channel, ChannelHandler)` / `UnregisterHandler` — один обработчик на канал; кадр не-`Control` канала без обработчика или не в `Ready` отбрасывается (debug-журнал). Исключение обработчика пишется в журнал и сессию не роняет.
- `Control`: `ping`, `pong`, `bye`, `pair-*` сессия обрабатывает сама; всё остальное уходит подписчикам `ControlMessageReceived`. Нечитаемое или неизвестное сообщение молча пропускается.
- «Вступительные заявления»: `LayoutMessage`, `InputMonitorsMessage`, `DeviceCapabilitiesMessage`, `DeviceInfoMessage`, `PeerVouchMessage` хранятся 30 с (`OpeningStatementLife`), не больше 32, и переигрываются каждому новому подписчику `ControlMessageReceived` — пир начинает говорить в момент `Ready`, раньше, чем функции успевают подписаться.
- `CloseAsync(reason)`: `bye` с тайм-аутом 1 с, отмена, ожидание циклов. Встречающиеся причины: `shutdown`, `duplicate`, `trusted`, `unpaired`, `disposed`.

**Сообщения.** `ping`, `pong`, `bye` и всё, что проходит через `ControlMessageReceived`.

**Ограничения и запасные пути.** Ядро не фильтрует управляющие сообщения по состоянию сессии: защита в том, что функции подписываются по событию `SessionReady` (для `PeerIntroductions` проверено, для остальных сервисов — не выяснено в рамках ядра). Поздний подписчик получает удержанные сообщения повторно, даже если их уже обработали другие, поэтому в удержание попадают только идемпотентные типы.

**Тесты.** `NodeIntegrationTests.AHandlerThatSubscribesLateStillGetsWhatThePeerSaidFirst`, `CodecTests` (кодек сообщений).

### Пульс, обнаружение мёртвой связи и прикладной пинг

**Что делает.** Сессия сама замечает, что связь тихо умерла (устройство уснуло на Wi-Fi), и закрывается за десятки секунд, а не за час. Попутно меряется задержка, и её можно измерить по запросу там, где ICMP закрыт.

**Как устроено.** `PeerSession.HeartbeatLoopAsync`: каждые `HeartbeatInterval` 5 с (в режиме экономии `HeartbeatOnBattery` 10 с) проверяется тишина — нет ни одного кадра любого канала дольше `IdleTimeout` 20 с → `TimeoutException`, сессия закрывается. Затем шлётся `ping` с меткой `Stopwatch.GetTimestamp()`; сама запись обязана завершиться за те же 20 с (`SentWithinAsync`), иначе сессия тоже закрывается. `pong` возвращает метку, `LastRoundTrip` обновляется (значения от 0 до 1 минуты). `PingAsync(timeout)` — разовый замер, `null` при тайм-ауте. Нижний уровень — TCP keepalive и `TCP_MAXRT` 10 с из `PeerConnection`.

**Сообщения.** `ping`, `pong`.

**Ограничения и запасные пути.** Зависшая запись не отменяется, а бросается (отмена посреди записи испортила бы TLS-поток); её завершает закрытие соединения.

**Тесты.** `HeartbeatTests` (`AHeartbeatThatIsTakenInTimeIsNothingToWorryAbout`, `AHeartbeatThatCannotBeWrittenInTimeEndsTheSession`, `WaitingForAHeartbeatEndsWithTheSession`); пинг — в `NodeIntegrationTests.TwoNodesDiscoverPairAndReconnect`.

### Менеджер сессий: автоподключение, переподключение, дубликаты

**Что делает.** С каждым устройством держится ровно одна сессия. Сопряжённые устройства соединяются сами, как только видят друг друга, и сами же восстанавливают связь после обрыва.

**Как устроено.** `src/WinMirroring.Core/Peers/PeerSessionManager.cs`.
- `OnPeerSeen(DiscoveredPeer)` обновляет `PeerDirectory`; если устройство сопряжено, сессии нет и попытка не идёт — проверяет, что отпечаток из маяка равен закреплённому (иначе предупреждение, соединения нет), и решает, кому соединяться.
- `ShouldInitiate`: соединяется устройство с меньшим id (порядковое сравнение строк). Другое ждёт `InitiatorFallbackDelay` 15 с с первого маяка без сессии и затем соединяется само (односторонняя достижимость, VPN). Между попытками не меньше `ReconnectInterval` 5 с.
- `ConnectAsync(peerId, endPoint, expectedFingerprint, ct)`: TLS с проверкой отпечатка, рукопожатие, проверка, что ответил именно ожидаемый id.
- `HandleInboundAsync(TcpClient)`: TLS-приём, рукопожатие ответчика, регистрация.
- `RegisterSessionAsync`: при двух одновременных сессиях остаётся та, которую открыло устройство с меньшим id (`IsCanonical`), другая закрывается с причиной `duplicate`. Заполняется `PeerDirectory`, при `Ready` запоминается адрес и поднимается `SessionReady`.
- События: `SessionReady`, `SessionClosed`, `PairedPeersChanged`, `UnpairedByPeer(name)`. Прочее: `Sessions`, `TryGetSession`, `IsPaired`, `RememberedDiscoveryEndPoints`, `IsRememberedAddress`, `PairAsync`, `Trust`, `UnpairAsync`, `CloseAllAsync`.

- Таймер `_silentPeers` (раз в `PeerExpiry / 4` = 2 с, живёт от конструктора до `DisposeAsync`) вызывает `PeerDirectory.ExpireUnseen(now, PeerExpiry)`: устройство в состоянии `Discovered`, от которого 8 с нет маяков, становится `Offline`. Исключения подписчиков `PeerDirectory.Changed` при этом ловятся и пишутся в журнал (вызов идёт с потока таймера).

**Ограничения и запасные пути.** Переподключение запускается только приходом маяка (широковещательного или ответа на зонд); собственного таймера повторов нет. При закрытии сессии устройство становится `Discovered`, если маяк был не старше `PeerExpiry` (8 с), иначе `Offline`; оставшись `Discovered` без новых маяков, оно станет `Offline` при ближайшем просмотре. Устройство на батарее шлёт маяк раз в 4 с, так что двух потерянных подряд маяков достаточно, чтобы оно на время показалось «не в сети». `_waitingSince` (начало ожидания для `InitiatorFallbackDelay`) при этом не сбрасывается.

**Тесты.** `NodeIntegrationTests.TwoNodesDiscoverPairAndReconnect`, `ADeviceThatStopsAnnouncingItselfIsNoLongerShownAsOnTheNetwork`.

### Каталог устройств (`PeerDirectory`, `PeerInfo`)

**Что делает.** Потокобезопасный список всего, что интерфейс показывает об устройствах: имя, отпечаток, сопряжено ли, состояние, адреса, когда видели в последний раз, последняя ошибка.

**Как устроено.** `src/WinMirroring.Core/Peers/PeerDirectory.cs`, `PeerInfo.cs`. `Update(id, mutate)` меняет запись под замком и поднимает `Changed` с копией; `Snapshot()` и `TryGet` отдают копии. Состояния `PeerState`: `Offline`, `Discovered`, `Connecting`, `Connected` (сессия без взаимного сопряжения), `Pairing`, `Ready`. `HasSession` = `Connected | Pairing | Ready`. `ExpireUnseen(now, after)` (internal, вызывает менеджер сессий по таймеру) переводит в `Offline` каждую запись в состоянии `Discovered`, у которой `LastSeen` пуст или старше `after`, и поднимает `Changed` по одному разу на устройство; остальные состояния (`Connecting` и все с сессией) по маякам не судятся, остальные поля записи (имя, адреса, `IsPaired`) не меняются.

**Ограничения и запасные пути.** Из каталога ничего не удаляется: `Remove(id)` существует, но в `src/` и `tools/` не вызывается, записи живут до конца процесса — замолчавшее устройство остаётся в списке как `Offline`. Удаление чужих (несопряжённых) устройств сознательно не сделано в ядре: приложение обрабатывает `Changed` только как добавление/обновление (`MainViewModel.Upsert`), а страница устройств (`HubUi`) сохраняет общую раскладку из тех устройств, что есть в списке, — устройство, пропавшее из каталога, при следующем сохранении выпало бы из раскладки у всех.

**Тесты.** `PeerDirectoryTests` (`ADeviceThatHasStoppedAnnouncingItselfIsNoLongerOnTheNetwork`, `APairedDeviceThatGoesQuietStaysListedAndStaysPaired`, `OnlyADeviceListedAsAnnouncingItselfIsJudgedByItsBeacons`, `ADeviceNeverHeardFromIsNotOnTheNetworkEither`); сквозной — `NodeIntegrationTests.ADeviceThatStopsAnnouncingItselfIsNoLongerShownAsOnTheNetwork`.

### Сопряжение подтверждением

**Что делает.** Пользователь на одном устройстве просит сопряжения, на другом нажимает «Принять»; после этого обе стороны закрепляют отпечаток сертификата друг друга. Оба экрана показывают одинаковый шестизначный код — его сравнивают глазами, ничего не вводят.

**Как устроено.**
- `src/WinMirroring.Core/Pairing/IPairingUserInterface.cs`: `ConfirmIncomingPairingAsync(PairingPrompt, ct)`, необязательные `PairingStarted` и `PairingFinished`; вызываются не из UI-потока. `PairingPrompt`: id, имя и отпечаток пира, свой отпечаток, код.
- `src/WinMirroring.Core/Pairing/PairingCrypto.cs`: `ConfirmationCode` = первые 4 байта SHA-256 от `"WinMirroring-confirmation-v1"` и двух отпечатков (в верхнем регистре, отсортированных) по модулю 1 000 000, в виде `"123 456"`; `GenerateNonce` — 32 случайных байта (вызов рукопожатия).
- Инициатор (`PeerSessionManager.PairAsync` → `PeerSession.PairAsync`): при отсутствии сессии соединяется по адресу из каталога, `Unpaired → Pairing`, шлёт `pair-request`, ждёт `pair-result` до `PairingTimeout` 2 минуты; по тайм-ауту или отмене шлёт `pair-cancel`.
- Ответчик (`HandlePairRequestAsync`, отдельная задача, чтобы диалог не блокировал пинг): спрашивает пользователя (тоже до 2 минут), при согласии сохраняет запись, шлёт `pair-result(true)` и только потом становится `Ready`. Инициатор сохраняет запись и становится `Ready` прямо в цикле приёма, до чтения следующего кадра.
- Сохранение (`OnPairedAsync`): `PairedPeer` с id, именем, отпечатком, временем, портами и адресом соединения.
- Причины неудачи: `not-pairable`, `rejected`, `timeout`, `cancelled`, `connection-lost`, `protocol-error`, `storage-error`.

**Настройки.** `AppSettings.PairedPeers`.

**Сообщения.** `pair-request`, `pair-result`, `pair-cancel`.

**Ограничения и запасные пути.** Сертификаты уже доказаны рукопожатием, поэтому «Принять» без сверки кода при активном посреднике делает посредника доверенным (осознанное решение, описано в `docs/ARCHITECTURE.md`). Запрос сопряжения может прислать любое устройство из допустимой подсети. В комментариях кода (`PairedPeer`, `PeerSessionManager.PairAsync`, заголовок `NodeIntegrationTests`) осталось слово «PIN», хотя PIN с протокола v3 нет.

**Тесты.** `PairingCryptoTests` (`BothDevicesShowTheSameCode`, `CodeIsSixDigitsInTwoGroups`, `AnotherCertificateGivesAnotherCode`), `NodeIntegrationTests` (`TwoNodesDiscoverPairAndReconnect`, `RejectedPairingIsNotStored`, `APairingThatWasRefusedIsNotTakenForOneThatWasBroken`).

### Разрыв сопряжения, в том числе «нас забыли»

**Что делает.** Разрыв сопряжения на одном устройстве доходит до второго: оно тоже забывает первое и сообщает об этом пользователю, даже если в момент разрыва было выключено.

**Как устроено.** `PeerSessionManager.UnpairAsync` удаляет запись, сохраняет настройки, закрывает сессию с причиной `unpaired`. Отдельного сообщения нет: `ForgetIfTheyHaveForgottenUs` смотрит на сессию, оказавшуюся `Unpaired`, при том что у нас запись о сопряжении есть (тот же отпечаток и id), и решает по правилу `WhatUnpairedMeans(pairedHereNow, saidPairedInHandshake, awaitingTheirTrust)`:

| Запись есть сейчас | Мы ответили «да» в рукопожатии | `AwaitingTheirTrust` | Вывод (`UnpairedMeans`) | Что делается |
|---|---|---|---|---|
| нет | — | — | `Nothing` | ничего: два чужих устройства |
| да | нет | — | `AskAgain` | запись появилась уже после рукопожатия этой сессии; сессия закрывается с причиной `trusted`, чтобы переподключение спросило обе стороны заново |
| да | да | да | `NotTheirsYet` | устройству доверились по слову третьего, и оно ещё ни разу не ответило «да»; запись сохраняется, сессия остаётся `Unpaired` |
| да | да | нет | `Forgotten` | «нет» сказала другая сторона, и раньше она нас знала: запись удаляется, поднимаются `PairedPeersChanged` и `UnpairedByPeer(name)` (приложение показывает уведомление) |

**Ограничения и запасные пути.** Устройство может заставить забыть только себя. Отказ в сопряжении не считается разрывом. Запись с `AwaitingTheirTrust` «нас забыли» не удаляет никогда — пока хотя бы одна сессия с этим устройством не станет `Ready`; если другая сторона успела довериться и снова разорвать сопряжение, не побывав с нами в `Ready`, односторонняя запись останется до ручного удаления.

**Тесты.** `NodeIntegrationTests.ForgettingADeviceReachesTheDeviceThatWasForgotten`, `APairingThatWasRefusedIsNotTakenForOneThatWasBroken`, `ANoInTheHandshakeOnlyMeansForgottenWhenTheyOnceKnewUs` (таблица правила), `ADeviceTrustedOnAnothersWordHasNotForgottenUsJustBecauseItHasNotHeardYet`.

### Знакомство по цепочке (`PeerIntroductions`)

**Что делает.** Сопряжённые устройства рассказывают друг другу, с кем ещё они сопряжены. Такое устройство показывается как «известное через …», и доверить ему можно одним нажатием, без подтверждения на обоих экранах; доверие становится взаимным через устройство-посредника.

**Как устроено.** `src/WinMirroring.Core/Peers/PeerIntroductions.cs`.
- При `SessionReady` каждая сторона шлёт `vouch` со списком своих сопряжённых (кроме адресата): id, имя, отпечаток, порты, последние адреса. Сообщение без `IsReply` получает ответный список; при изменении списка сопряжённых всем готовым сессиям уходит обновление с `IsReply = true`.
- Принимается только от взаимно сопряжённого пира в `Ready` (`IsTrusted`). Новый список пира заменяет прежний; уже сопряжённые и собственная запись отбрасываются. При закрытии сессии всё, что известно через неё, удаляется. `Snapshot()`, `TryGet`, событие `Changed`.
- `TrustAsync(peerId)`: `PeerSessionManager.Trust(…, awaitingTheirs: true)` закрепляет отпечаток (запись в `PairedPeers`, адреса; открытая неготовая сессия с этим устройством закрывается с причиной `trusted`, чтобы переподключение пришло уже `Ready`), затем посреднику уходит `vouch-relay(ToPeerId, собственная запись)`. Новая запись помечается `PairedPeer.AwaitingTheirTrust = true`: дальняя сторона о нас ещё не знает. Пометка снимается первой же сессией `Ready` с этим устройством (и обычным сопряжением подтверждением); уже существующая запись с тем же отпечатком от повторного доверия ждущей не становится.
- Посредник пересылает `vouch-relay` дальше только по правилу `MayPassOn`: отправитель и адресат оба сопряжены с ним, адресат — не сам отправитель, а запись — собственная запись отправителя (id и отпечаток совпадают с сессией). Адресат, получив реле от сопряжённого пира, доверяет записанному устройству (`Trust(…, awaitingTheirs: false)`: отправитель реле доверился первым, так что сопряжение уже взаимное).

**Сообщения.** `vouch`, `vouch-relay`.

**Ограничения и запасные пути.**
- Обратное доверие дойдёт, только если у посредника в этот момент есть готовая сессия с дальним устройством; `vouch-relay` отправляется один раз и не повторяется. Иначе доверие остаётся односторонним: запись у доверившей стороны сохраняется (`AwaitingTheirTrust`), прямая сессия с дальним устройством приходит `Unpaired` и такой остаётся. Раньше в этом месте срабатывало «нас забыли»: только что созданная запись удалялась, а пользователь получал уведомление о разрыве сопряжения. Выйти из одностороннего состояния можно обычным сопряжением подтверждением или доверием со стороны дальнего устройства (ему нас тоже представляет посредник).
- Если прямое соединение установилось раньше, чем дошло реле, ничего не теряется: дальняя сторона, получив реле, закрывает неготовую сессию (`trusted`), и переподключение приходит `Ready`. Если реле пришло посреди рукопожатия (запись появилась после того, как сторона уже ответила «нет»), сессия закрывается по правилу `AskAgain` — с тем же итогом.
- Устройство старой версии на любом из концов ведёт себя по-старому: у него «нас забыли» может сработать преждевременно. Формат сообщений не менялся.

**Тесты.** `NodeIntegrationTests.ADeviceTrustedByBothEndsIntroducesThemToEachOther`, `ADeviceMayOnlyEverPassOnItsOwnRecord`, `ADeviceTrustedOnAnothersWordHasNotForgottenUsJustBecauseItHasNotHeardYet` (прямое соединение раньше реле, затем реле, затем обычный разрыв), `ANoInTheHandshakeOnlyMeansForgottenWhenTheyOnceKnewUs`.

### UDP-путь для медиа (`MediaSocket`)

**Что делает.** Один UDP-сокет на узел для данных реального времени — звука и кадров экрана: потерянный пакет не задерживает следующий. Сокет принимает всё подряд; подлинность проверяют владельцы потоков.

**Как устроено.** `src/WinMirroring.Core/Media/MediaSocket.cs`. Привязка к `0.0.0.0` на номере транспортного порта (UDP 47621 по умолчанию), буферы приёма и отправки по 1 МБ, `SIO_UDP_CONNRESET` выключен. `PacketReceived(IPEndPoint from, ReadOnlySpan<byte>)` вызывается из цикла приёма (обработчик должен быть быстрым и не хранить span). `Send` — синхронный `SendTo`, ошибки только в debug-журнал. Наружу через `MirroringNode.MediaPacketReceived`, `SendMedia`, `MediaPort`; подписаны `AudioService` и `ScreenStreamingService`.

**Ограничения и запасные пути.** Нет фильтра по подсетям и нет привязки к выбранным адресам. Пересоздаётся при перезапуске сети. Сокет один на узел, поэтому звук и видео к одному устройству для Windows — один поток (см. «Приоритет медиа»).

**Тесты.** Нет (ни в проекте ядра, ни в других тестовых проектах `MediaSocket` напрямую не упоминается).

### Шифрование медиа-датаграмм (`MediaCipher`)

**Что делает.** Каждая UDP-датаграмма зашифрована и аутентифицирована ключом своего потока. Ключ создаёт отправитель и передаёт внутри TLS-сессии, поэтому UDP не нужно собственное рукопожатие.

**Как устроено.** `src/WinMirroring.Core/Media/MediaCipher.cs`. AES-256-GCM, ключ 32 байта (`GenerateKey`). Пакет: `0xA7` (магия), `kind` (1), `streamId` (4, big-endian), `sequence` (8, big-endian) | шифротекст | тег 16 байт; `HeaderSize` 14, `Overhead` 30. Заголовок — аутентифицируемые данные; nonce = `streamId ‖ sequence` (12 байт). `Seal`, `TryReadHeader`, `TryOpen` (false для поддельного, испорченного или чужого пакета).

**Сообщения.** Ключ передаётся в `AudioStartMessage.Key` и `ScreenStreamMessage.MediaKey`.

**Ограничения и запасные пути.** Экземпляр не потокобезопасен — на каждое направление свой. Уникальность (streamId, sequence) на время жизни ключа — обязанность вызывающего; защиты от повторов в самом шифре нет.

**Тесты.** В проекте ядра нет; `MediaCipher` проверяется в `tests/WinMirroring.Audio.Tests/AudioPipelineTests.cs` (в том числе чужой ключ и разбор заголовка) и используется в `tests/WinMirroring.Screen.Tests/ResendTests.cs`.

### Приоритет медиа (`MediaPriority`)

**Что делает.** Просит Windows отправлять медиа-датаграммы к данному устройству — все, и звук, и видео экрана — раньше остального трафика этого устройства, что важно на Wi-Fi. Звук впереди видео это **не** ставит.

**Как устроено.** `src/WinMirroring.Core/Media/MediaPriority.cs` (internal, владеет им `MediaSocket`). qWAVE: `QOSCreateHandle`, `QOSAddSocketToFlow` с типом `QOSTrafficTypeVoice` (4) и флагом non-adaptive, плюс попытка DSCP 46 (`QOSSetFlow`), которая проходит только с правами администратора. Поток задаётся парой (сокет, адрес с портом). Счётчик пользователей на адрес: второй запрос на тот же адрес нового потока не создаёт, а только увеличивает счётчик; `Remove` снимает поток, когда пользователей не осталось. Вызывают `AudioSender` и `VideoLink` через `MirroringNode.PrioritizeMedia`/`ReleaseMediaPriority`, оба с `session.PeerTransportEndPoint`.

**Ограничения и запасные пути.**
- Полностью «по возможности»: нет `qwave.dll`, служба выключена или отказ — тихо ничего не происходит.
- Медиа-сокет у узла один, звук и видео уходят с него на один и тот же адрес и порт, поэтому это один поток с одним классом: голосовой класс получают оба, кто бы его ни запросил, и между собой они идут в порядке записи в сокет. Звук оказывается впереди картинки, только пока картинка идёт по TCP-соединению сессии (JPEG-плитки; видео, пока UDP-путь `VideoLink` не подтверждён или отдыхает), и впереди прочего трафика машины. Поставить звук впереди видео при одном сокете средствами qWAVE нельзя — понадобился бы отдельный сокет для одного из двух, а это изменение отправки и приёма медиа, а не этого класса. Комментарии в коде приведены в соответствие.
- Многомегабитное видео тем самым идёт в голосовом классе Wi-Fi — так вышло, а не было выбрано; правильный ли это класс для видео, в коде не решается.

**Тесты.** Нет.

### Энергосбережение (`PowerMode`)

**Что делает.** На батарее устройство делает то же реже: маяки, пульс, проверка сети, частота кадров. Режим включается сам на устройстве с батареей.

**Как устроено.** `src/WinMirroring.Core/Power/PowerMode.cs`. `OnBattery` — `GetSystemPowerStatus`, `AcLineStatus == 0`; ответ кэшируется на 5 с; «неизвестно» (255) и ошибки считаются питанием от сети. `Saving` — по `PowerSettings.Saving`: `Never` / `OnBattery` / `Always`. `Fps(configured)` = `min(configured, max(1, Power.Fps))` в режиме экономии. `Every(usually, whileSaving)` выбирает интервал. В ядре: маяк 2 → 4 с, пульс 5 → 10 с, проверка сети 3 → 10 с. Вне ядра `Power` используют `ScreenSource` и `ScreenStreamingService`.

**Настройки.** `PowerSettings.Saving` (`OnBattery`), `PowerSettings.Fps` (30).

**Тесты.** `PowerModeTests` (`SavingFollowsTheSettingAndWhatTheDeviceIsPluggedInto`, `TheFrameRateIsOnlyEverLowered`, `AFrameRateOfNothingIsRefused`, `TheLongerIntervalIsUsedOnlyWhileSaving`, `ADeviceSavesOnItsBatteryWithoutBeingTold`).

### Правила брандмауэра (`Hosting/FirewallRules`)

**Что делает.** Добавляет входящие правила брандмауэра Windows для приложения через запрос прав администратора.

**Как устроено.** `src/WinMirroring.Core/Hosting/FirewallRules.cs`. `AddAsync(exe, tcpPort, udpPort)` запускает `cmd /c` с `runas`: удаляет и заново создаёт правила `WinMirroring (TCP in)` (TCP `tcpPort`) и `WinMirroring (UDP in)` (UDP `udpPort` и `tcpPort`), для программы `exe`, профили `private,domain`. Приложение передаёт транспортный порт и порт обнаружения. Отказ в UAC → false. `ExistAsync` ищет правило через `netsh ... show rule`.

**Ограничения и запасные пути.** Результат — код возврата последней команды цепочки. `ExistAsync` в `src/` и `tools/` не вызывается и опирается на английский текст «No rules match». Публичный профиль сети не покрыт.

**Тесты.** Нет.

### Локальный интерфейс: проверка собеседника по именованному каналу (`Local/PipePeer`)

**Что делает.** Позволяет приложению и его помощникам с повышенными правами убедиться, что на другом конце именованного канала именно ожидаемый exe. Это единственное, что ядро даёт для связи процессов на одной машине.

**Как устроено.** `src/WinMirroring.Core/Local/PipePeer.cs`. `TryGetServerProcessId`/`TryGetClientProcessId` (`GetNamedPipe*ProcessId`), `ImagePath(pid)` (`OpenProcess` с `PROCESS_QUERY_LIMITED_INFORMATION` + `QueryFullProcessImageNameW`), `Is(pid, expectedImage)` — сравнение полных путей без учёта регистра; невозможность узнать считается отказом. Используется в `src/WinMirroring.Input/Injection/InjectionPipe.cs` и `src/WinMirroring.Shell/Elevated/` (`ElevatedHelper`, `ElevatedShellHost`).

**Ограничения и запасные пути.** Сами каналы и локальный интерфейс CLI (`WinMirroring.Shell.Local.LocalShellClient`, которым `wm-cli --exec` обращается к запущенному приложению) находятся вне ядра. Для узла на той же машине ядро даёт `NodeOptions.IncludeLoopback` и `SettingsStore.IsInUseByAnotherNode`.

**Тесты.** Нет.

### Константы протокола

`src/WinMirroring.Core/Protocol/ProtocolConstants.cs`.

| Константа | Значение | Смысл |
|---|---|---|
| `Version` | 4 | 2: движение мыши с метками времени и keepalive; 3: сопряжение подтверждением; 4: большие сообщения частями и запасной кадр в звуковых пакетах |
| `DefaultDiscoveryPort` | 47620 | UDP, обнаружение |
| `DefaultTransportPort` | 47621 | TCP (сессии) и UDP (медиа) |
| `MaxFrameSize` | 16 МБ | предел кадра и собранного сообщения; отправитель отказывается слать больше |
| `TlsTargetHost` | `winmirroring` | SNI и SAN сертификата |
| `BeaconMagic` | `WMIR` | начало маяка |
| `MulticastGroup` | 239.255.47.62 | группа обнаружения |
| `BeaconInterval` / `BeaconOnBattery` | 2 с / 4 с | период маяка |
| `PeerExpiry` | 8 с | «видели недавно»: столько без маяков — и устройство без сессии считается не в сети |
| `HeartbeatInterval` / `HeartbeatOnBattery` | 5 с / 10 с | период пульса |
| `IdleTimeout` | 20 с | тишина, после которой сессия закрывается |
| `HandshakeTimeout` | 15 с | TLS и прикладное рукопожатие (каждое) |
| `PairingTimeout` | 2 мин | ожидание ответа на сопряжение |
| `ConnectTimeout` | 5 с | установка TCP |

### Управляющие сообщения (`ControlMessage`)

**Что делает.** Все сообщения канала `Control` — JSON с дискриминатором типа `t`. Неизвестный или испорченный JSON даёт `null`, поэтому более новое устройство не может уронить старое.

**Как устроено.** `src/WinMirroring.Core/Messages/ControlMessage.cs`, `ControlMessageCodec.cs`. Имена полей camelCase, чтение без учёта регистра, `null` при записи опускается; `byte[]` идёт как base64. Новые типы дописываются в список `JsonDerivedType`; новые поля добавляются со значениями по умолчанию.

| `t` | Запись | Поля (по умолчанию) | Назначение | Кто использует |
|---|---|---|---|---|
| `hello` | `HelloMessage` | `DeviceId, DeviceName, ProtocolVersion, Certificate, Challenge?, TransportPort, DiscoveryPort` | первое сообщение в каждую сторону; сервер добавляет вызов | `PeerHandshake` |
| `auth` | `AuthMessage` | `Signature, ServerFingerprint, Paired` | подпись клиента и «я вас закрепил» | `PeerHandshake` |
| `auth-result` | `AuthResultMessage` | `Ok, Paired, Reason?` | итог рукопожатия | `PeerHandshake` |
| `pair-request` | `PairRequestMessage` | — | просьба принять сопряжение | `PeerSession` |
| `pair-result` | `PairResultMessage` | `Ok, Reason?` | принято или нет | `PeerSession` |
| `pair-cancel` | `PairCancelMessage` | `Reason?` | инициатор передумал или истёк срок | `PeerSession` |
| `ping` | `PingMessage` | `Timestamp` | пульс и замер задержки | `PeerSession` |
| `pong` | `PongMessage` | `Timestamp` | ответ на `ping` | `PeerSession` |
| `bye` | `ByeMessage` | `Reason?` | вежливое закрытие | `PeerSession` |
| `layout` | `LayoutMessage` | `Version, Cells, Origin?, Placements?` | раскладка устройств; побеждает новейшая (версия, затем автор), пересылается дальше | `InputSharingService` |
| `input-monitors` | `InputMonitorsMessage` | `Monitors: List<LayoutMonitor>, IsReply=false` | мониторы отправителя для раскладки | `InputSharingService` |
| `caps` | `DeviceCapabilitiesMessage` | `HasVirtualDisplay, IsReply=false, Monitors?: List<RemoteMonitor>, CanEncodeH264=false, CanDecodeH264=false, DrawsPointer=false, CanEncodeHevc=false, CanDecodeHevc=false` | возможности: виртуальный дисплей, мониторы, кодеки, рисует ли указатель сам | `ScreenStreamingService` |
| `device-info` | `DeviceInfoMessage` | `AppVersion, IsReply=false` | версия приложения | `DeviceInfoService` (App) |
| `vouch` | `PeerVouchMessage` | `Peers: List<VouchedPeer>, IsReply=false` | список сопряжённых отправителя | `PeerIntroductions` |
| `vouch-relay` | `VouchRelayMessage` | `ToPeerId, Peer: VouchedPeer` | «передай дальше»: обратное доверие через посредника | `PeerIntroductions` |
| `audio-start` | `AudioStartMessage` | `StreamId, Key, Codec, SampleRate, Channels, FrameMs, Automatic=false, Microphone=false` | начало звукового потока (с `Microphone` — это микрофон отправителя, место ему в виртуальном кабеле, а не в динамиках); ключ AES-256-GCM, кодек `opus` или `pcm16` | `AudioService`, `AudioSender` |
| `audio-stop` | `AudioStopMessage` | `StreamId` | конец звукового потока | `AudioService`, `AudioSender` |
| `audio-route` | `AudioRouteRequestMessage` | `Listen, Refuse=false` | пожелание получателя: слать сюда / не слать / автоматически | `AudioService` |
| `audio-loss` | `AudioLossReportMessage` | `StreamId, PercentLost, JitterMs` | отчёт получателя о потерях и джиттере | `AudioService` |
| `mic-offer` | `MicrophoneOfferMessage` | — | «можно прислать тебе мой микрофон?»; старая версия не отвечает, и это значит «нет» | `AudioService` |
| `mic-answer` | `MicrophoneAnswerMessage` | `Accepted, Device=null, Reason=null` | ответ: куда будет воспроизводиться, или почему нет (`off`, `no-device`) | `AudioService` |
| `screen-request` | `ScreenRequestMessage` | `StreamId, MonitorIndex, MonitorKey?, MaxFps?` | зритель просит поток монитора | `ScreenStreamingService`, `ScreenViewer` |
| `screen-stream` | `ScreenStreamMessage` | `StreamId, Kind, Title, MonitorIndex, MonitorCount, ControlAllowed, Minimized=false, ScalePercent=100, Codec="jpeg", MonitorKey="", MediaKey="", CropTags=false, MediaReplies=false` | поток начался или изменился; `Kind`: `monitor`/`window`/`display` | `ScreenSource`, `ScreenStreamingService`, `ScreenViewer` |
| `screen-stop` | `ScreenStopMessage` | `StreamId, Reason?` | поток закончен (любая сторона) | `ScreenStreamingService`, `ScreenViewer` |
| `screen-keyboard` | `ScreenKeyboardMessage` | `StreamId, Show` | фокус попал в поле ввода: показать или убрать клавиатуру зрителя | `ScreenStreamingService` |
| `screen-stats` | `ScreenStatsMessage` | `StreamId, AckMs, ViewerMs, EncoderMs, Kbps, Fps, Udp, SharePercent, GaveUp, Resent` | показатели потока раз в секунду | `ScreenSource`, `ScreenViewer`, окно просмотра |
| `screen-mark` | `ScreenMarkMessage` | `StreamId, Note` | отметка «подвисло» в журналах обеих сторон | `ScreenStreamingService`, `ScreenViewer` |
| `screen-thumbs` | `ScreenThumbsMessage` | `StreamId, On, Width=0` | включить или выключить миниатюры остальных мониторов; `Width` — какой ширины (0 — обычные 256 px) | `ScreenStreamingService`, `ScreenViewer` |
| `screen-thumb` | `ScreenThumbMessage` | `StreamId, MonitorKey, Width, Height, Jpeg` | одна миниатюра | `ScreenSource`, `ScreenViewer` |
| `screen-foreground` | `ScreenForegroundMessage` | `StreamId, Process` | активная программа на источнике | `ScreenSource`, `ScreenStreamingService` |
| `screen-fallback` | `ScreenCodecFallbackMessage` | `StreamId, Reason` | у зрителя нет картинки из видео, просит перейти на другой кодек | `ScreenStreamingService`, `ScreenViewer` |
| `input-release` | `InputReleaseRequestMessage` | — | управляемое устройство просит забрать ввод | `InputSharingService` |
| `input-blocked` | `InputBlockedMessage` | `Window` | окно администратора глотает ввод | `InputSharingService`, `ScreenSource` |
| `display-invite` | `DisplayInviteMessage` | — | «стань моим вторым монитором» | `ScreenStreamingService.Display` |
| `display-extend` | `DisplayExtendRequestMessage` | `Width, Height, ScalePercent` | «расширь рабочий стол на меня» | `ScreenStreamingService.Display` |
| `display-result` | `DisplayExtendResultMessage` | `Ok, Error?` | расширение не началось | `ScreenStreamingService.Display` |
| `kbd-layout` | `KeyboardLayoutMessage` | `Layout` (младшие 32 бита HKL) | раскладка клавиатуры в обе стороны | `InputSharingService.KeyboardLayout` |
| `log-request` | `LogRequestMessage` | `RequestId` | запрос хвоста журнала | `DiagnosticsService` (App) |
| `log-response` | `LogResponseMessage` | `RequestId, FileName, Text, Error?` | хвост журнала | `DiagnosticsService` (App) |

Вложенные записи: `VouchedPeer(Id, Name, Fingerprint, TransportPort, DiscoveryPort, Addresses?)`, `RemoteMonitor(Index, Label, Primary, Key="")`, `LayoutMonitor(Key, Label, Primary, Width=0, Height=0, Left=0, Top=0)`.

**Тесты.** `CodecTests` (`ControlMessagesRoundTrip`, `ControlMessageCodecReturnsNullForUnknownOrBrokenInput`, `CapabilitiesCarryTheMonitorList`, `MonitorListsAndPerMonitorLayoutCellsRoundTrip`, `AKeyboardHintRoundTripsAndAnUnknownMessageIsDroppedQuietly`, `ALayoutFromAnOlderDeviceHasWholeDeviceCells`, `ScreenStreamMessageFromOlderPeerDefaultsToJpeg`).

### Настройки: классы и свойства

Все классы — в `src/WinMirroring.Core/Settings/`. Ядро само читает только идентичность, порты, интерфейсы, `PinConnectionsToInterface`, `FindPeersOnTailscale`, ручные адреса, `PairedPeers` и `Power`; остальное хранится для других модулей.

**`AppSettings`** (`AppSettings.cs`). Методы `FindPairedById`, `FindPairedByFingerprint` (без учёта регистра).

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `DeviceId` | `""` → GUID при первом запуске | постоянный id устройства |
| `DeviceName` | `""` → имя машины | отображаемое имя |
| `CertificateThumbprint` | `""` | отпечаток сертификата в `CurrentUser\My` |
| `DiscoveryPort` | 47620 | UDP-порт обнаружения |
| `TransportPort` | 47621 | TCP-порт сессий и UDP-порт медиа |
| `SelectedInterfaceIds` | `[]` | выбранные интерфейсы; пусто — все поднятые |
| `PinConnectionsToInterface` | `true` | привязывать соединение к интерфейсу пира |
| `FindPeersOnTailscale` | `true` | искать сопряжённые устройства в сети Tailscale по имени |
| `ManualPeerAddresses` | `[]` | `host` или `host:discoveryPort` для зондов |
| `PairedPeers` | `[]` | сопряжённые устройства |
| `StartWithWindows` | `false` | автозапуск |
| `AutoUpdate` | `true` | проверять GitHub Releases и ставить обновления в простое |
| `MenuButtonX`, `MenuButtonY` | −1, −1 | положение плавающей кнопки меню (−1: угол по умолчанию) |
| `MenuButtonHidden` | `false` | кнопка скрыта пользователем |
| `Input` | `new InputSettings()` | общий ввод и раскладка |
| `Clipboard` | `new ClipboardSettings()` | буфер обмена |
| `Audio` | `new AudioSettings()` | звук |
| `Screen` | `new ScreenSettings()` | экран |
| `ButtonBar` | `new ButtonBarSettings()` | панель кнопок |
| `Shell` | `new ShellSettings()` | удалённые команды |
| `Power` | `new PowerSettings()` | энергосбережение |

**`PairedPeer`** (`PairedPeer.cs`). Метод `RememberAddress(address, keep = 5)`.

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `Id` | `""` | id устройства |
| `Name` | `""` | имя (обновляется при каждой готовой сессии) |
| `Fingerprint` | `""` | закреплённый отпечаток сертификата |
| `PairedAt` | — | время сопряжения |
| `AwaitingTheirTrust` | `false` | устройству доверились по слову третьего (`PeerIntroductions.TrustAsync`), и оно ещё ни разу не подтвердило сопряжение со своей стороны; пока так, его «не сопряжены» в рукопожатии не считается разрывом. В файле от прежней версии свойства нет — читается как `false`; прежняя версия, читая новый файл, свойство пропускает |
| `TransportPort` | 47621 | порт сессий пира |
| `DiscoveryPort` | 47620 | порт обнаружения пира |
| `LastKnownAddresses` | `[]` | до 5 последних IPv4, новые первыми |

**`InputSettings`** (`InputSettings.cs`).

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `Enabled` | `true` | ставить хуки и участвовать в общем вводе |
| `EdgeResistance` | 8 | сколько «сырых» отсчётов мыши нужно продавить в край для перехода |
| `SmoothingEnabled` | `true` | адаптивный буфер сглаживания движения на управляемом устройстве |
| `MaxSmoothingDelayMs` | 30 | верхняя граница этого буфера |
| `SyncKeyboardLayout` | `true` | синхронизировать раскладку клавиатуры |
| `ElevatedInjector` | `false` | вводить через помощника с правами администратора |
| `LayoutVersion` | 0 | версия раскладки (Unix-мс, всегда выше виденных) |
| `LayoutOrigin` | `""` | автор текущей раскладки (разрешение ничьей) |
| `Layout` | `[]` | сетка старого формата, выводится из `Placements` |
| `Placements` | `[]` | свободная расстановка; пусто — действует сетка |

Записи раскладки там же: `LayoutCell(DeviceId, Column, Row, Monitor = null)`, `LayoutPlacement(DeviceId, X, Y, Monitors = null)`, `LayoutRect(Key, Left, Top, Width, Height)`.

**`ClipboardSettings`** (`MediaSettings.cs`).

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `Enabled` | `true` | общий буфер обмена |
| `ShareFiles` | `true` | предлагать скопированные файлы и папки |
| `MaxInlineMegabytes` | 64 | текст и картинки крупнее не отправляются |
| `MaxDirectPasteMegabytes` | 64 | наборы файлов до этого размера предлагаются и как `CF_HDROP` |
| `DragDropEnabled` | `true` | перетаскивание файлов через край экрана |

**`ScreenSettings`** (`MediaSettings.cs`). Перечисление `VideoCodec`: `Auto`=0, `Jpeg`=1, `H264`=2, `Hevc`=3.

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `AllowViewing` | `true` | сопряжённые устройства могут смотреть этот экран |
| `AllowControl` | `true` | зрители могут управлять |
| `MaxFps` | 60 | целевая частота кадров |
| `Quality` | 70 | качество JPEG-плиток, 30–95 |
| `VideoCodec` | `Auto` | способ кодирования |
| `VideoBitrateKbps` | 12000 | битрейт видео для полного экрана |
| `CloseUnusedViewMinutes` | 5 | закрывать свёрнутый просмотр через столько минут (0: никогда) |
| `WindowDragEnabled` | `true` | перетаскивание окна за заголовок через край отправляет его на устройство |
| `HideSentWindow` | `true` | прятать отправленное окно на виртуальный монитор |
| `AutoKeyboardOnTap` | `false` | касание поля ввода там открывает клавиатуру здесь |
| `KeyboardButton` | `true` | круглая кнопка клавиатуры поверх картинки |
| `KeyboardButtonX`, `KeyboardButtonY` | 0.97, 0.62 | её положение в долях свободного места |
| `DirectPresent` | `true` | рисовать кадры прямо с видеокарты |
| `DirectPresentBrokeIn` | `""` | версия, в которой вывод с видеокарты уронил отрисовку WPF на этом устройстве; пишет сама программа, пока совпадает с запущенной версией — кадры идут через память |
| `PageGrips` | `true` | ручки у левого и правого края вида для перехода между мониторами (на сенсорном экране) |
| `PageGripsY` | `0.5` | их место вдоль края в долях свободной высоты; меняется перетаскиванием ручки |
| `ThreeFingerSwipe` | `false` | переход между мониторами свайпом тремя пальцами; до 1.0.79 — `SwipeMonitors`, `true` |
| `ShowStats` | `false` | строка показателей поверх картинки |
| `DictationButton` | `true` | кнопка голосового ввода |
| `SharpStill` | `true` | чёткий снимок неподвижного экрана |

**`ShellSettings`** (`MediaSettings.cs`).

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `AllowCommands` | `false` | сопряжённое устройство может запускать здесь команды |
| `AllowElevatedCommands` | `false` | …и консоль с правами администратора (с подтверждением каждый раз) |

**`AudioSettings`** (`MediaSettings.cs`).

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `SendEnabled` | `true` | разрешить отправку звука |
| `FollowInput` | `true` | звук идёт устройству, которое управляет этим |
| `PinnedTargetId` | `""` | постоянный получатель (пусто: нет) |
| `FollowScreenViewer` | `true` | звук идёт устройству, которое смотрит этот экран |
| `MuteLocalWhileSending` | `true` | глушить локальные динамики на время передачи |
| `ReceiveEnabled` | `true` | воспроизводить присланный звук |
| `BitrateKbps` | 160 | битрейт |
| `BufferMs` | 40 | цель джиттер-буфера |
| `MutedByApp` | `false` | флаг «динамики заглушены нами» для восстановления после сбоя |
| `AcceptMicrophone` | `true` | принимать микрофон других устройств (в виртуальный кабель) |
| `MicrophoneOutput` | `""` | id или имя выхода для чужого микрофона; пусто — искать виртуальный кабель |

**`ButtonBarSettings`, `ButtonPreset`, `ButtonBinding`** (`ButtonBarSettings.cs`). Хранятся на устройстве, не синхронизируются.

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `ButtonBarSettings.Visible` | `true` | панель показана |
| `ButtonBarSettings.ActivePreset` | `"Основные"` (`ButtonPreset.MainName`) | выбранный набор |
| `ButtonBarSettings.Presets` | `ButtonPreset.CreateDefaults()` | «Основные» (10 кнопок), «Браузер» (8), «Текст» (10) |
| `ButtonPreset.Name` | `""` | имя набора |
| `ButtonPreset.Buttons` | `[]` | кнопки |
| `ButtonPreset.Programs` | `""` | имена процессов через запятую, пробел или `;` — набор включается сам, когда программа активна |
| `ButtonBinding.Label` | `""` | подпись |
| `ButtonBinding.Keys` | `""` | аккорды по порядку (`Ctrl+K, Ctrl+C`); одни модификаторы — залипающая кнопка |
| `ButtonBinding.Text` | `null` | текст, который печатается после клавиш |

`ButtonPreset.ProgramNames()`, `Normalize` (нижний регистр, без `.exe`), `For(presets, process)` (первый набор, назвавший программу), `Clone`.

**`PowerSettings`** (`PowerSettings.cs`). Перечисление `PowerSaving`: `Never`=0, `OnBattery`=1, `Always`=2.

| Свойство | По умолчанию | Смысл |
|---|---|---|
| `Saving` | `OnBattery` | когда экономить |
| `Fps` | 30 | кадров в секунду в режиме экономии (и отправка, и запрос) |

**Тесты.** `ButtonBarSettingsTests` (`AFreshDeviceHasTheStandardPresetsShown`, `SavedPresetsComeBackAsTheyWereWithoutTheStandardOnesAddedAgain`, `SettingsFromAnOlderVersionGetTheStandardPresets`, `ACloneDoesNotShareButtons`), `ButtonPresetProgramTests` (`AProgramGetsTheSetThatNamesIt`, `AProgramNoSetNamesGetsNone`, `ASetWithNoProgramsIsNeverChosenByItself`, `TheFirstSetThatNamesAProgramWins`, `ACopyOfASetIsForTheSamePrograms`), `PowerModeTests`.

### Сводка тестов ядра

| Класс | Что покрывает | Explicit |
|---|---|---|
| `CodecTests` | маяк, кадры и части, кодек управляющих сообщений | нет |
| `PairingCryptoTests` | код подтверждения, разбор ручного адреса | нет |
| `HeartbeatTests` | срок на запись пульса (`SentWithinAsync`) | нет |
| `IPv4SubnetTests` | арифметика подсетей | нет |
| `InterfacePinTests` | выбор интерфейса, привязанный сокет | нет |
| `TailscalePeerTests` | разбор `tailscale status --json`, выбор машин | нет |
| `PowerModeTests` | режим экономии, частота кадров, интервалы | нет |
| `DataDirectoryTests` | занятость каталога данных | нет |
| `UnreadableSettingsTests` | нечитаемый файл настроек убирается в сторону и не затирается | нет |
| `PeerDirectoryTests` | замолчавшее устройство без сессии становится `Offline` и остаётся в списке | нет |
| `ButtonBarSettingsTests`, `ButtonPresetProgramTests` | панель кнопок | нет |
| `NodeIntegrationTests` | два-три настоящих узла на loopback: обнаружение зондом, сопряжение, переподключение, отказ, удержанные сообщения, «нас забыли», цепочка доверия (в том числе прямое соединение раньше реле), замолчавшее устройство, сообщение больше предела, нечитаемые настройки, переименование, двойной `Dispose`, смена сети | нет |

Без собственных тестов в ядре: `MediaCipher`, `MediaSocket`, `MediaPriority`, `FirewallRules`, `PipePeer`, `DeviceIdentityStore`, `NetworkInterfaceCatalog`, `PeerHandshake` (последние три — только косвенно через интеграционные тесты).

### Расхождения с README и ARCHITECTURE

- `docs/ARCHITECTURE.md` §4: «слушатели биндятся только на выбранные адреса, никогда на 0.0.0.0». В коде так делает только TCP (`PeerListener`); приём обнаружения и `MediaSocket` привязаны к `IPAddress.Any`.
- §4: «TCP + TLS 1.3». В коде `SslProtocols.None` — версию выбирает ОС.
- §4: «Файлы — отдельное TCP + TLS соединение на передачу». В коде одно соединение и канал `ChannelId.File` (§10 того же документа уже говорит о канале File).
- §5.4 упоминает `ScreenCapabilityMessage`; такого типа нет, согласование идёт в `DeviceCapabilitiesMessage` (§10 называет его верно).
- `README.md`: «по умолчанию не больше 30 кадров/с». `ScreenSettings.MaxFps` по умолчанию 60; 30 — это `PowerSettings.Fps` в режиме экономии.
- `README.md` о Tailscale: сравнивается «имя компьютера». В коде имя машины Tailscale сравнивается с `PairedPeer.Name` — именем устройства в WinMirroring, которое можно изменить.
- Ни README, ни ARCHITECTURE не описывают: знакомство по цепочке (`PeerIntroductions`, `vouch`/`vouch-relay`), двусторонний разрыв сопряжения (`UnpairedByPeer`), энергосбережение (`PowerMode`, `PowerSettings`), защиту каталога данных (`SettingsStore.IsInUseByAnotherNode`), таймер слежения за сетью, срок на запись пульса, канал `Shell` и `ShellSettings`, версию протокола 4.
