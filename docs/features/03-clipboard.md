# Буфер обмена, файлы и перетаскивание (WinMirroring.Clipboard)

> Часть справочника [docs/FEATURES.md](../FEATURES.md). Составлено по коду версии 1.0.75; источник правды — код.

Проект `src/WinMirroring.Clipboard` (≈5 500 строк) содержит всё, что связано с общим буфером обмена, передачей файлов и OLE-перетаскиванием. Хореография мыши для перетаскивания и решение «что делать с присланными файлами» живут в `src/WinMirroring.App/Services/DragDropCoordinator*.cs` и `PeerFilesService.cs`; здесь они описаны в той мере, в какой нужны для понимания потока данных. Управляющих сообщений канала Control эта область не использует: всё идёт по каналам `ChannelId.Clipboard` (= 2) и `ChannelId.File` (= 3) поверх `PeerSession`. Истории буфера обмена нет — хранится только строка «последнее действие».

### Служба ClipboardSyncService: запуск и привязка к сессиям

**Что делает.** Один singleton на приложение. Слушает локальный буфер, рассылает его всем подключённым сопряжённым устройствам, применяет чужой, отдаёт и забирает файлы, держит движок перетаскивания.

**Как устроено.** `src/WinMirroring.Clipboard/ClipboardSyncService.cs` (+ partial `ClipboardSyncService.SendFiles.cs`). `Start()`: `OfferDownloader.CleanUp()` → запуск `ClipboardThread` → создание `DragDropEngine` (ждёт инициализацию до 5 с; при неудаче `Drag == null`, буфер продолжает работать) → подписка на `SessionReady`/`SessionClosed` → фоновый `PublishLoopAsync`. На каждую готовую сессию регистрируются обработчики каналов Clipboard и File и создаётся `FileFetcher` (словарь `_fetchers`); наличие fetcher-а и есть признак «с этим устройством можно обмениваться файлами» (`SourceFor(deviceId)`, `SendFilesAsync`). Исходящие снимки идут через `Channel<ClipboardSnapshot>` ёмкостью 1 с `DropOldest`: ждёт только самый свежий. Настройки читаются из `_node.Settings.Clipboard` при каждом использовании, отдельного «применить» нет. `DisposeAsync` идемпотентен.

**Ограничения и запасные пути.** `SessionReady` приходит после смены состояния сессии, поэтому код, смотрящий только на `SessionState.Ready`, может застать службу ещё не привязанной (комментарий в `OnSessionReady`). `Drag` выставляется, даже если окно поверхности не создалось (`DragSurface.IsAvailable` никто не проверяет) — тогда ловля просто возвращает `null`.

**Тесты.** `ClipboardSyncLiveTests.TextSentByAPeerLandsOnTheClipboard` — `[Fact(Explicit = true)]`.

### Слежение за локальным буфером и защита от эха

**Что делает.** Замечает, что пользователь что-то скопировал, и не принимает за «копирование» собственные записи, пришедшие с другого устройства.

**Как устроено.** `ClipboardThread.cs`: отдельный STA-поток `WinMirroring.Clipboard` с `OleInitialize`, message-only окном (класс с уникальным именем на экземпляр) и `AddClipboardFormatListener`. `WM_CLIPBOARDUPDATE` взводит таймер `DebounceMs = 150`; по таймеру `HandleChange()`. Защита от эха — номер последовательности: после каждой своей записи (`WriteItems`, `SetDataObject`) и при старте запоминается `_ignoreUpToSequence = GetClipboardSequenceNumber()`; изменение с номером не больше этого игнорируется. Все чтения/записи буфера и все OLE-объекты выполняются на этом потоке через `InvokeAsync` (очередь + `PostMessage(WM_APP+1)`). Открытие буфера: 10 попыток по 20 мс (`OpenWithRetry`), `OleSetClipboard` — столько же при `CLIPBRD_E_CANT_OPEN`. При остановке потока, если в буфере всё ещё наш `IDataObject`, выполняется `OleSetClipboard(null)` — виртуальные файлы из буфера убираются.

**Ограничения и запасные пути.** То, что лежало в буфере до запуска приложения, не отправляется (номер запомнен при старте) — для этого есть ручная отправка, см. ниже. Если буфер занят дольше 200 мс, изменение пропускается без повтора (`"Clipboard is busy; skipping this change"`). Копирование пользователя, попавшее между нашей записью и чтением номера, будет проигнорировано.

**Тесты.** `ClipboardThreadLiveTests.WriteIsSeenByAnotherListenerButNotEchoedByItself` — `[Fact(Explicit = true)]` (трогает настоящий буфер).

### Синхронизация данных: текст, HTML, RTF, изображения

**Что делает.** Скопированный текст или картинка появляется в буфере всех подключённых устройств. Мелкое уходит сразу, крупное (скриншот) получатели вытягивают кусками, чтобы не задерживать мышь на том же TCP-соединении.

**Как устроено.** Форматы (`ClipboardModel.cs`, `Native/ClipboardFormats.cs`), значения enum — идентификаторы на проводе: `UnicodeText = 1` (CF_UNICODETEXT), `Html = 2` («HTML Format»), `Rtf = 3` («Rich Text Format»), `Png = 4` («PNG»), `Dib = 5` (CF_DIB). Байты передаются как есть из HGLOBAL. Чтение идёт в порядке `ClipboardFormats.Shared` с общим бюджетом `MaxInlineBytes`: формат, не влезающий в остаток бюджета, пропускается, остальные отправляются. Если в буфере есть `CF_HDROP`, читается только список файлов, прочие форматы игнорируются. `ClipboardCodec.EncodeData`: `[1][flags]` + (`u16 count`, затем на элемент `u8 kind, i32 length, bytes`); при сыром размере больше `CompressAbove = 8 КБ` тело сжимается zlib (`CompressionLevel.Fastest`, флаг `0x01`). Закодированное сообщение до `InlineLimit = 256 КБ` идёт целиком (`DataMessage`); больше — кладётся в `LocalOfferStore.AddBlob` (хранятся последние `MaxBlobs = 4`) и уходит `DataRefMessage` `[3] i64 blobId, i64 length`. Получатель читает blob по каналу File обычными чанк-запросами с `index = 0` через `RemoteFileStream` (чанк `256 КБ`, до `Window = 8` запросов в полёте), затем декодирует как обычное сообщение. Применение — `ClipboardThread.WriteItems`: `EmptyClipboard` + `SetClipboardData` на каждый формат. Неизвестные `kind` при декодировании пропускаются.

Порядок применения — `ArrivalOrder.cs`. Каждое входящее сообщение с содержимым буфера (`Data`, `DataRef`, `Offer`) получает номер прихода ещё в цикле приёма (`ArrivalOrder.Next()`), а применяется по-прежнему в своём `Task.Run`. Непосредственно перед записью, уже на потоке буфера, номер сверяется (`ArrivalOrder.Apply` внутри `ClipboardThread.WriteItemsAsync(items, order, arrival)` / `SetDataObjectAsync(dataObject, order, arrival)`): если уже применено что-то с большим номером, запись не делается — побеждает пришедшее позже. Применённым считается только то, что действительно записано: более новое сообщение, которое не удалось записать или оказалось пустым, старое не блокирует. Локальное копирование пользователя (`LocalChanged`) тоже берёт номер (`CopiedHere()`), поэтому чужое содержимое, которое в этот момент ещё докачивается, поверх него уже не ляжет. `DataRef`, обогнанный ещё до начала скачивания, не скачивается вовсе.

**Настройки.** `ClipboardSettings.Enabled` (true) — выключает и отправку, и приём данных и файловых предложений. `MaxInlineMegabytes` (64; служба зажимает в 1…1024, UI принимает 1…1024) — бюджет на чтение и предел на приём.

**Сообщения.** Clipboard: `DataMessage = 1`, `DataRefMessage = 3`. File: `FileCodec.RequestMessage = 1` / `ResponseMessage = 2`.

**Ограничения и запасные пути.** Приём: `DataRef` длиннее `MaxInlineBytes + 1024` игнорируется; распаковка zlib обрывается при превышении `MaxInlineBytes` (несжатое тело так не проверяется, его ограничивает кадр `ProtocolConstants.MaxFrameSize = 16 МБ`). Если ни один формат не влез в бюджет, ничего не отправляется и ничего не сообщается. Обогнанное содержимое отбрасывается молча для пользователя (только запись в журнал «newer content is there already»); начатое скачивание `DataRef` при этом не прерывается, результат просто не применяется. Номера прихода — порядок, в котором сообщения дошли до этого устройства, а не время копирования: локальное копирование замечается с задержкой `DebounceMs`, и чужое сообщение, применённое внутри этого окна, его перезапишет. Комментарий у `Shared` говорит «richer formats first», но первым идёт простой текст.

**Тесты.** `CodecTests`: `SmallDataRoundTripsUncompressed`, `LargeDataIsCompressedAndRespectsTheLimit`, `TruncatedDataIsRejected`, `OfferAndReferenceRoundTrip`, `FileMessagesRoundTrip`. `ArrivalOrderTests`: `ContentAppliedInTheOrderItArrivedIsAllApplied`, `WhatArrivedEarlierButIsReadyLaterIsNotApplied`, `ANewerOneThatCouldNotBeWrittenDoesNotKeepAnOlderOneOut`, `WhatTheUserCopiesHereIsNewerThanWhatIsStillOnItsWayIn`, `OfManyAppliedAtOnceTheLastToArriveIsWhatStays` (без настоящего буфера: проверяется сама очерёдность). `ClipboardSyncLiveTests.TextSentByAPeerLandsOnTheClipboard` (Explicit).

### Запрет источника на передачу (пароли)

**Что делает.** Если программа (менеджер паролей) пометила скопированное как не подлежащее обработке, оно не читается и не уходит на другие устройства; пользователю сообщается причина.

**Как устроено.** `Native/ClipboardPrivacy.cs` + `ClipboardThread.WhySyncIsRefused`. Проверяются три формата: наличие `ExcludeClipboardContentFromMonitorProcessing`; `CanUploadToCloudClipboard == 0`; `CanIncludeInClipboardHistory == 0`. Проверка идёт до чтения содержимого — данные не попадают в память процесса. Формат-разрешение, который есть, но не читается или короче 4 байт, считается нулём (запрет). Возвращается `ClipboardSnapshot` с `Refused`; `PublishAsync` пишет в активность «Не отправлено: …» (строки `MonitorsForbidden`, `DevicesForbidden`, `HistoryForbidden`).

**Настройки.** Отдельной нет; сообщение появляется только при `Enabled = true`.

**Тесты.** `ClipboardPrivacyTests`: `NothingMarkedMeansNothingForbidden`, `ContentExcludedFromMonitorsIsLeftAlone`, `ContentThatMayNotLeaveTheMachineIsNotSent`, `ContentThatMayNotBeRememberedIsNotSentEither`, `AnyValueButZeroAllowsIt`. `ClipboardThreadLiveTests.MarkedContentIsNeitherReadNorSent` — Explicit.

### Копирование файлов: предложение (offer) и LocalOfferStore

**Что делает.** При копировании файлов и папок их содержимое никуда не отправляется. Другим устройствам уходит только опись (манифест); содержимое остаётся на источнике, пока кто-то не вставит.

**Как устроено.** `Files/LocalOfferStore.cs`. `Create(paths, sourceName, pinned)` рекурсивно раскрывает папки (родитель раньше детей; дочерние reparse-точки пропускаются, скрытые и системные файлы включаются, недоступное молча пропускается) в список `FileOfferEntry(Path, Size, IsDirectory, LastWriteUtcTicks)`, где `Path` относительный, с `\`. Совпадающие имена верхнего уровня получают суффикс ` (2)`, корень диска `D:\` получает имя `D`. Идентификатор — случайный `Int64`. Пределы: `MaxEntries = 50 000` записей на предложение (лишнее отбрасывается молча), `MaxOffers = 8` предложений (самые старые незакреплённые вытесняются). Манифест `FileOfferManifest(OfferId, SourceName, Entries)` уходит как `OfferMessage` — `[2]` + JSON (camelCase). Удалённая сторона может адресовать только пару (offerId, index) сохранённого предложения — произвольный путь назвать нельзя.

**Настройки.** `ShareFiles` (true) — и отправка, и приём предложений из буфера. `Enabled` — общий выключатель.

**Сообщения.** Clipboard: `OfferMessage = 2`.

**Ограничения и запасные пути.** Одно хранилище на всё: каждое перетаскивание через край/в окно просмотра (`CreateLocalDragPayload`) и каждая адресная отправка тоже создают предложение, то есть вытесняют «восемь последних копирований». Закреплённые (`pinned`) предложения не вытесняются до `Release`. Манифест больше 16 МБ не отправляется: «Буфер обмена слишком большой для отправки (…)». Привязки предложения к конкретному пиру нет — любой подключённый сопряжённый узел, знающий `offerId`, может читать. В JSON манифеста попадают и вычисляемые свойства записи (`totalSize`, `topLevel`) — следует из поведения `System.Text.Json` по умолчанию, запуском не проверялось. Если нет ни одной готовой сессии, предложение не создаётся вовсе.

**Тесты.** `FilesTests`: `OfferExpandsFoldersParentFirstAndServesOnlyItsFiles`, `DuplicateTopLevelNamesGetSuffixes`. `SendFilesIntegrationTests.APinnedOfferSurvivesLaterCopiesUntilItIsReleased`. `CodecTests.OfferAndReferenceRoundTrip`, `UnsafeOfferEntriesAreRejected`.

### Вставка файлов: виртуальные файлы (VirtualFileDataObject)

**Что делает.** На принимающем устройстве скопированные файлы лежат в буфере как «виртуальные»: Проводник при вставке сам вытягивает каждый файл потоком и показывает свой диалог копирования. До вставки по сети не идёт ничего.

**Как устроено.** `Files/VirtualFileDataObject.cs` — COM `IDataObject`, кладётся в буфер через `OleSetClipboard`. Форматы в порядке перечисления: `FileGroupDescriptorW` (HGLOBAL; по 592 байта на запись, флаги `FD_ATTRIBUTES | FD_WRITESTIME | FD_UNICODE`, для файлов ещё `FD_FILESIZE | FD_PROGRESSUI`), `FileContents` (IStream, `lindex` = индекс записи), `Preferred DropEffect` = copy и, если разрешена прямая вставка, `CF_HDROP`. `FileContents` отдаёт `ComStream` (`Files/ComStream.cs`, только чтение, с `Seek`, `Stat`, `CopyTo`) поверх потока от `OfferPrefetcher.Open(index)` либо, если тот вернул `null`, поверх `RemoteFileStream`. `SetData` от Проводника («Paste Succeeded», «Performed DropEffect») принимается и отбрасывается. `Files/FormatEnumerator.cs` — `IEnumFORMATETC`.

Время жизни. Объект владеет своим `OfferPrefetcher` и сам принадлежит тому, кто его выставил: потоку буфера (`ClipboardThread._ownDataObject`) или перетаскиванию (`DragDropEngine.StartDragAsync`). `Retire()` — «больше не предлагается»: вызывается, когда в буфере оказалось что-то другое (`LetGoOfOwnDataObject` в `HandleChange`, `WriteItems`, `SetDataObject`) и когда `DoDragDrop` вернулся. Уже начатая вставка при этом продолжается (Проводник держит объект сколько ему нужно), сокращается только ожидание читателя, см. `OfferPrefetcher`. `Dispose()` — «до объекта больше не добраться»: вызывается из `ClipboardThread.Cleanup` при остановке потока и останавливает prefetcher сразу.

**Ограничения и запасные пути.** Имя в дескрипторе обрезается до 259 символов (при том что `IsSafeEntry` допускает путь до 1024). Отмена — только средствами самого Проводника. О том, что Проводник отпустил поток (`ComStream`), объект не узнаёт: COM об этом не сообщает, `ComStream` вложенный поток не закрывает. Вставить можно, пока предложение живо на источнике и приложение там запущено; иначе поток бросает «The other device no longer offers this file (its clipboard changed)». Про запуск от администратора: README и ARCHITECTURE утверждают, что тогда вставка и перетаскивание из обычного Проводника не работают; в коде проекта этому ничего не соответствует (ограничение Windows), не проверялось.

**Тесты.** `FilesTests.DataObjectDescribesAndStreamsFiles`.

### «Прямая вставка»: CF_HDROP и кэш (OfferDownloader)

**Что делает.** Небольшие наборы файлов можно вставить и в программы, понимающие только настоящие пути. В момент, когда программа запрашивает пути, набор целиком скачивается в кэш, и программе отдаются пути в кэше.

**Как устроено.** `ClipboardSyncService.AllowsDirectPaste(manifest)`: `TotalSize ≤ MaxDirectPasteMegabytes` и записей не больше `MaxDirectPasteEntries = 5000`. `VirtualFileDataObject.EnsureDownloaded()` (под блокировкой, результат запоминается) вызывает `OfferDownloader.DownloadAsync` в `Task.Run` и синхронно ждёт. `Files/OfferDownloader.cs`: корень — `%LocalAppData%\WinMirroring\cache\clipboard\{offerId:x16}` (или заданная папка); для каждой записи проверяется, что полный путь остаётся внутри корня; папки создаются заранее; файл, который уже есть с тем же размером, пропускается. Дальше один пакет (bundle) от первого недостающего файла до конца: файлы до `SmallFile = 1 МБ` собираются в памяти и пишутся до `ParallelWrites = 6` потоками, большие пишутся по мере прихода в `*.part` и переименовываются; размер сверяется с объявленным, выставляется время изменения. Принятые байты каждого файла считаются по мере прихода: первая же запись `Data`, выводящая файл за объявленный размер, обрывает передачу («The other device sent more of «…» than it announced»), начатый `*.part` удаляется. Прогресс — не чаще раза в 100 мс. `CleanUp()` при запуске службы удаляет папки кэша старше суток (по времени создания).

**Настройки.** `MaxDirectPasteMegabytes` (64; UI принимает 0…4096; 0 фактически отключает `CF_HDROP`). Действует и на перетаскивание.

**Ограничения и запасные пути.** Если источник не умеет пакеты — `ReceiveChunkByChunkAsync` (по файлу через `RemoteFileStream`). Скачивание для `CF_HDROP` идёт с токеном источника `IRemoteFileSource.Closed` (у `FileFetcher` он отменяется в `Close()` — при закрытии сессии и при остановке службы, что наступит раньше); отменить его со стороны пользователя по-прежнему нельзя, Проводник такой возможности не даёт. Любая ошибка скачивания (включая отмену) завершает индикатор (`Finished`, `Ok = false`) и отдаётся программе как `E_FAIL`; результат при ошибке не запоминается, следующая попытка докачивает недостающее. Кэш чистится только при старте приложения, не по таймеру. Какой из двух путей (виртуальные файлы или `CF_HDROP`) выберет конкретная программа, включая Проводник, — не выяснено. Недостаток байт по-прежнему обнаруживается только на записи `End`; файл, для которого `End` пришёл, отправитель может прислать повторно — он будет записан заново (память и диск при этом не растут).

**Тесты.** `FilesTests`: `DownloaderRecreatesTheTree`, `DropFilesRoundTrip`, `DownloaderStopsAtTheFirstByteBeyondTheAnnouncedSize` (малый файл в памяти и большой на диске). `BundleTransferTests`: `AFolderOfManySmallFilesAndALargeOneArrivesExactly`, `ADeviceThatDoesNotSendBundlesIsReadChunkByChunk`.

### Передача содержимого чанками (FileCodec, FileServer, FileFetcher, RemoteFileStream)

**Что делает.** Базовый способ: получатель запрашивает диапазоны байт файла, источник отвечает. Сейчас это путь для blob-ов буфера, для устройств старой версии и запасной путь при любых нестандартных чтениях.

**Как устроено.** `Protocol/FileCodec.cs`: запрос `[1] u32 id, i64 offer, i32 index, i64 offset, i32 length` (29 байт), ответ `[2] u32 id, u8 status, data`; `FileChunkStatus`: `Ok = 0`, `NotFound = 1`, `IoError = 2`; `MaxChunk = 256 КБ`. `Files/FileServer.cs` держит до `MaxOpenHandles = 4` открытых файлов (простой `HandleIdle = 30 с`), читает через `RandomAccess`, открывает с `FileShare.ReadWrite | Delete`. `Files/FileFetcher.cs` (`IRemoteFileSource`) сопоставляет ответы по id, тайм-аут ответа `ResponseTimeout = 30 с`, при закрытии сессии все ожидания падают с `IOException`, после чего отменяется токен `Closed` (член `IRemoteFileSource` с реализацией по умолчанию `CancellationToken.None`) — для тех, кто в этот момент ничего не читает и сам о закрытии не узнал бы. `Files/RemoteFileStream.cs` — поток с перемоткой, держит до `Window = 8` запросов впереди читателя; перемотка или короткий чанк перезапускают конвейер.

**Ограничения и запасные пути.** Короткий чанк до объявленного конца — продолжение с фактической позиции; пустой — «The remote file is shorter than announced»; чанк длиннее запрошенного — «The other device sent more of the file than was asked for» (запрос никогда не выходит за объявленный размер, значит это данные за концом файла). Сервер файлов не зависит от настроек буфера: отвечает всегда, но только по сохранённым предложениям.

**Тесты.** `FilesTests`: `RemoteStreamReadsAndSeeksAcrossChunks`, `RemoteStreamReportsAnExpiredOffer`, `RemoteStreamRefusesMoreThanItAskedFor`. `FileTransferIntegrationTests.FileIsPulledOverTheSession`. `CodecTests.FileMessagesRoundTrip`.

### Передача пакетом (BundleCodec, BundleSender, BundleReader) и OfferPrefetcher

**Что делает.** Весь набор файлов идёт одним потоком без запроса на каждый кусок: тысячи мелких файлов не упираются в задержку сети. Проводник по-прежнему берёт файлы по одному, но поток бежит впереди него.

**Как устроено.** `Protocol/BundleCodec.cs`, канал File: `Request [3] u32 id, i64 offer, i32 first, i32 end, i32 window, u8 flags` (`FlagBrotli = 1`); `Start [4] id, status`; `Block [5] id, u8 codec, i32 rawLength, payload`; `Ack [6] id, i64 consumed`; `End [7] id, status`; `Cancel [8] id`. Блок после декодирования — целые записи `u8 kind, i32 index, i32 length, bytes` (`Data = 1`, `End = 2`, `Error = 3`), запись не пересекает границу блока; `BlockSize = 1 МБ`. `Files/BundleSender.cs` — три стадии параллельно: чтение (файлы до 1 МБ целиком, `ParallelReads = 8`, запас в памяти `ReadAheadBytes = 48 МБ`; большие — потоком), упаковка и сжатие (Brotli quality 1, window 22; сжатый вариант уходит, только если меньше `WorthCompressing = 0.92` исходного; блоки, где больше половины байт из файлов с «уже сжатыми» расширениями, не пробуются; после 4 несжавшихся подряд пробуется каждый восьмой), отправка по порядку не дальше окна получателя. Получатель просит окно `FileFetcher.BundleWindow = 16 МБ`; `BundleReader` подтверждает каждые четверть окна. Нечитаемый файл → запись `Error`, остальные идут дальше. `Files/OfferPrefetcher.cs`: первый открытый Проводником файл запускает пакет «этот файл и все после»; принятое ждёт в памяти до `Budget = 96 МБ`; пропуск вперёд больше чем на `RestartAfterSkipping = 32 МБ` запускает новый пакет.

Чем заканчивается пакет prefetcher-а. Проводник не сообщает, что бросил вставку, и отпущенный им поток здесь не закрывается, поэтому «насос» (`Pump`) завершается сам:
- пакет принят целиком — `Finished(true)`;
- пакет оборвался (ошибка отправителя, запись за пределами описи, данных больше объявленного размера файла) — `Finished(false)`, недополученные файлы дочитываются чанками;
- источник закрылся (`IRemoteFileSource.Closed`: сессия закрыта или служба остановлена) — насос будится, даже если стоит на бюджете и сам ничего не читает;
- никто не читает: насос упёрся в бюджет и за `StalledAfter = 2 мин` (после `Retire()` — `StalledAfterRetiring = 15 с`) Проводник не открыл ни одного файла и не забрал ни байта — вставка считается брошенной, всё накопленное освобождается, пакет отменяется (`Cancel` отправителю);
- `Dispose()` (остановка потока буфера через `VirtualFileDataObject.Dispose`).

Во всех этих случаях, если прогресс уже объявлялся, приходит `Finished` — индикатор скрывается. Исключение одно: новый насос сменил старый внутри той же передачи (пропуск вперёд, повторная вставка) — тогда прогресс просто продолжается. Если Проводник всё же вернётся после «брошенной» вставки (человек долго отвечал на вопрос о замене файла), открытые потоки дочитываются чанками с текущей позиции, а следующий открытый файл запускает новый пакет.

**Ограничения и запасные пути.** Устройство старой версии на `Request` не отвечает: через `BundleStartTimeout = 5 с` для этой сессии ставится `_noBundles`, дальше только чанки. Повторное открытие файла, перемотка, уже пройденный файл, оборвавшийся пакет → обычный `RemoteFileStream` с текущей позиции. Файл, часть которого насос уже принял и не сохранил (буфер был отпущен), при повторном открытии читается чанками, а не из пакета с середины. Если читатель ждёт данных, а насос стоит на бюджете, значит бюджет занят потоками, которые Проводник открыл и бросил недочитанными: такие буферы сразу освобождаются (их читатель, если он жив, продолжит чанками), ожидающий получает свой файл без задержки. Отправитель ждёт подтверждений до `StalledAfter = 10 мин` (Проводник может ждать ответа человека), затем `End(IoError)`; получатель сдаётся раньше (2 мин), платя за ложное срабатывание повторной передачей уже накопленного (до 96 МБ). При отмене (`Cancel`, закрытие сессии) `End` не посылается. Если пакет принят целиком (оставшееся уместилось в бюджет), а вставка брошена, принятое остаётся в памяти, пока жив объект в буфере обмена.

**Тесты.** `BundleTransferTests`: `AFolderOfManySmallFilesAndALargeOneArrivesExactly`, `TextIsCompressedOnTheWayAndAlreadyCompressedDataIsNot`, `ExplorerReadingFileByFileIsServedFromOneBundleEvenWhenItSkipsOne`, `ADeviceThatDoesNotSendBundlesIsReadChunkByChunk`. `PrefetcherLifetimeTests` (источник в памяти `ScriptedSource`, без сети): `APasteGivenUpHalfWayEndsTheBundleAndSaysSo`, `AnOfferNoLongerOnTheClipboardIsGivenUpSooner`, `AClosedConnectionEndsABundleThatWasWaitingForAReader`, `DisposingEndsTheBundleAndSaysSo`, `AStreamLeftUnreadDoesNotKeepTheNextOneWaiting`, `AFileWhoseBeginningWasLetGoIsNotServedFromItsMiddle`, `MoreOfAFileThanWasAnnouncedStopsTheBundleAtOnce`.

### Перетаскивание файлов через край экрана

**Что делает.** Пользователь начинает тянуть файлы в Проводнике и, не отпуская кнопку, уводит курсор на соседнее устройство; отпускает над папкой или окном — файлы скачиваются с источника в момент броска. Работает в обе стороны и по цепочке устройств; Esc отменяет.

**Как устроено.** `DragDrop/DragSurface.cs` — почти невидимое (alpha 1/255) topmost-окно 96×96 на потоке буфера, зарегистрированное как `IDropTarget`, в двух ролях. *Catch*: ставится под курсор, в `DragEnter` узнаёт, что тащат — наш собственный `VirtualFileDataObject` (тогда `DragCatch.Forwarded` с исходными `SourceId` и манифестом) либо `CF_HDROP` (`DragCatch.LocalPaths`); бросок не принимает никогда (`Drop` → `DROPEFFECT_NONE`). *Origin*: ставится под курсор перед синтетическим нажатием кнопки, чтобы оно не кликнуло по чужому окну; живёт `OriginLifetimeMs = 500`. После `DragLeave` прячется через `HideAfterLeaveMs = 1500`. `DragDrop/DragDropEngine.cs`: `CatchAsync(at, timeout, afterShown)`, `ShowOriginAsync`, `HideSurfaceAsync`, `StartDragAsync(payload, source, offerDropFiles)` — ждёт левую кнопку до `ButtonWait = 250 мс`, затем `DoDragDrop` с `VirtualFileDataObject` и `DROPEFFECT_COPY`; `DropSource` бросает при отпускании кнопки, отменяет по Esc или `CancelActiveDrag()`. Одновременно один drag.

Сценарии (`src/WinMirroring.App/Services/DragDropCoordinator.cs`, `CatchTimeout = 300 мс`):
- *С управляющего на управляемое.* `InputSharingService.FocusChanged(peer)` при физически зажатой левой кнопке (и не при перетаскивании окна) → ловля под «припаркованным» курсором (`CursorParkPoint`, с `NudgeCursor`) → `CreateLocalDragPayload` (создаёт предложение, `SourceId` = свой id) → `DragStartMessage`. Управляемое принимает только от текущего контроллера (`_input.ControllerId`) и только если подключено к `SourceId`; ставит origin, инъецирует нажатие (`track: true`), запускает drag.
- *С управляемого на управляющее.* Курсор упирается в край при зажатой кнопке → `InputSharingService.BeforeEdgeHandOff` (ждёт до 1 с) → сначала попытка отдать перетаскиваемое окно (`TrySendMovingWindowAsync`), иначе ловля в точке края → `DragOutMessage` раньше, чем `EdgeReached`. Drag-сообщения обрабатываются прямо в цикле приёма, чтобы порядок сохранился. Когда курсор возвращается, управляющее запускает локальный drag (если файлы не его собственные); если курсор сразу ушёл на третье устройство — тому уходит `DragStartMessage` с исходным `SourceId`.
- *Возврат.* Собственный локальный drag на управляющем не прерывался и продолжается. На управляемом при уходе управления `ControlEnding` → `CancelActiveDrag()`.

**Настройки.** `DragDropEnabled` (true) — проверяется и при приёме сообщений в службе, и в координаторе. От `Enabled` и `ShareFiles` не зависит. `MaxDirectPasteMegabytes` определяет, получит ли drag ещё и `CF_HDROP`.

**Сообщения.** Clipboard: `DragStartMessage = 4`, `DragOutMessage = 5` — `[type]` + JSON `DragPayload(SourceId, Manifest)`.

**Ограничения и запасные пути.** Ловятся только перетаскивания с `CF_HDROP` (и наши собственные); текст и чужие виртуальные файлы не распознаются. Только копирование. Устройство-получатель должно иметь сессию с `SourceId`, иначе «not connected to it». Раскрытие дерева папок в `CreateLocalDragPayload` выполняется внутри той же секунды, что отведена `BeforeEdgeHandOff`; по истечении `EdgeReached` уходит в любом случае. ARCHITECTURE помечает проверку на двух физических устройствах как не выполненную для буфера; про drag отдельной пометки нет — не выяснено.

**Тесты.** Только кодек: `CodecTests.DragMessagesRoundTripAndRejectUnsafePaths`. Тестов на `DragSurface`, `DragDropEngine`, `DragDropCoordinator` нет.

### Перетаскивание в окно просмотра экрана и из него

**Что делает.** Файлы, брошенные из Проводника на изображение чужого экрана, «падают» в ту же точку на том устройстве. Обратно: если начать тянуть файлы внутри изображения и вывести курсор за его край, перетаскивание продолжится на этом устройстве.

**Как устроено.** `DragDropCoordinator.Viewer.cs` + `Views/ScreenViewerWindow.xaml.cs`. *Внутрь (DropIn):* WPF `Drop` на `Surface` (только `DataFormats.FileDrop`; нужно `ControlEnabled` и картинка) → нормализованные `x, y` → `DropIntoViewerAsync` → `ViewerDropMessage`. Транслирующее устройство (`DropFromViewerAsync`, по одному за раз, очередь до 5 с): `TryMapToScreen(..., bringForward: true)` → курсор в точку → origin → инъекция нажатия → `StartDragAsync` → через 120 мс поверхность убирается, курсор сдвигается на +4/+4 и обратно (60 и 250 мс), кнопка отпускается; результат ждётся до 2 мин. *Наружу (DragOut):* левая кнопка зажата и курсор ушёл дальше `DragOutMargin = 12` px за изображение → один раз за выход `CatchFromViewerAsync` → `ViewerCatchMessage`; транслирующее устройство ловит в этой точке (`ViewerCatchTimeout = 400 мс`) и всегда отвечает `ViewerCaughtMessage` (payload или `null`); просмотрщик ждёт `ViewerReplyTimeout = 3 с`, затем шлёт туда отпускание кнопки (тамошний drag заканчивается на ловушке без эффекта), снимает захват мыши и запускает локальный drag (`StartViewerDragAsync`).

**Настройки.** `DragDropEnabled`.

**Сообщения.** Clipboard: `ViewerDropMessage = 6`, `ViewerCatchMessage = 7`, `ViewerCaughtMessage = 8` — JSON `ViewerDragMessage(StreamId, X, Y, RequestId, Payload)`.

**Ограничения и запасные пути.** Бросок вне изображения — подсказка «Отпустите файлы на изображении». Если поток закрыт или управление не разрешено — игнорируется с записью в журнал. Комментариев «не проверено» в коде нет; README оговаривает, что функция работает только при обновлённых обоих устройствах.

**Тесты.** `CodecTests.ViewerDragMessagesRoundTripAndRejectUnsafePaths`; механика не покрыта.

### Адресная отправка файлов: «Открыть файл на устройстве» и обновление соседа

**Что делает.** Файлы можно послать одному устройству мимо буфера: чтобы они там открылись, или чтобы передать ему свою сборку WinMirroring для установки. Принимающая сторона спрашивает пользователя; без согласия ничего не скачивается и не запускается.

**Как устроено.** `ClipboardSyncService.SendFiles.cs`. `SendFilesAsync(peerId, paths, purpose, note)` создаёт закреплённое предложение, шлёт `SendFilesMessage` и ждёт ответ до `SendFilesTimeout = 15 мин` (ответ приходит после завершения скачивания), затем `Release`. Приёмник поднимает `FilesOffered` на рабочем потоке; обработчик вызывает `AcceptFilesAsync(session, request, targetDirectory)` (скачивание `OfferDownloader` в указанную папку, прогресс в `TransferProgress`) или `DeclineFiles(reason)`. `SendFilesPurpose`: `Open = 0`, `Update = 1`. Потребитель — `src/WinMirroring.App/Services/PeerFilesService.cs`: `Open` → `MessageBox` «Принять и открыть?» → папка `%LocalAppData%\WinMirroring\received\yyyy-MM-dd HH-mm-ss` → `Process.Start` (ShellExecute) на каждый элемент верхнего уровня; `Update` → отказ, если версия не новее; иначе вопрос → `%TEMP%\WinMirroring-update-{guid}` → `GithubUpdater.Apply(zip)`. Отправка сборки: zip собственной папки установки во временный файл.

**Настройки.** Нет; настройками буфера не управляется (комментарий: «not gated by the clipboard settings»).

**Сообщения.** Clipboard: `SendFilesMessage = 9` (JSON `SendFilesRequest(RequestId, Purpose, Note, Manifest)`), `SendFilesResultMessage = 10` (JSON `SendFilesResult(RequestId, Ok, Error)`).

**Ограничения и запасные пути.** Нет обработчика `FilesOffered` → немедленный ответ «это устройство не принимает файлы напрямую». Нет сессии → «устройство не на связи»; нечего слать → «нечего отправлять»; тайм-аут → «устройство не ответило». `AcceptFilesAsync` отвечает отправителю отказом при любом исключении скачивания (текст — `ex.Message`; неожиданные типы пишутся в журнал как ошибка), а при успехе сначала отвечает, потом сообщает пользователю; обработчик `TransferProgress`, бросивший исключение, приём не срывает. Если сессия закрылась до ответа, отправитель узнаёт об этом сам: `OnSessionClosed` завершает ожидание результатом «связь с устройством прервалась» (ответ приходит только по той сессии, по которой ушло предложение); тем же текстом отвечает `AcceptFilesAsync`, вызванный уже после закрытия. Пока принимающая сторона ждёт решения пользователя и связь жива, отправитель по-прежнему ждёт до тайм-аута. Очистки папки `received` в коде не найдено.

**Тесты.** `SendFilesIntegrationTests`: `FilesTheReceiverTakesArriveWholeAndTheSenderIsTold`, `ARefusedOfferTransfersNothingAndSaysWhy`, `ADeviceThatCannotTakeFilesAnswersInsteadOfLeavingTheSenderWaiting`, `AFailureNobodyExpectedIsToldToTheSenderAllTheSame`, `ASenderIsToldAtOnceWhenTheConnectionGoesBeforeTheAnswer`, `SendingToADeviceThatIsNotConnectedFailsAtOnce`, `APinnedOfferSurvivesLaterCopiesUntilItIsReleased`, `TheMessagesRoundTripAndNonsenseIsRefused`.

### Повторная отправка буфера вручную

**Что делает.** Кнопка «Отправить буфер обмена ещё раз» в окне просмотра отправляет текущее содержимое — на случай, если скопировали до подключения.

**Как устроено.** `ClipboardSyncService.PublishNowAsync()` → `ClipboardThread.ReadNowAsync()` → снимок в ту же очередь. Вызывается из `ScreenViewerWindow.OnSendClipboard`. Возвращает `false`, если буфер пуст или занят («Буфер обмена пуст или недоступен»). Уходит всем подключённым устройствам, не только тому, чьё окно открыто.

**Тесты.** Нет.

### Активность и индикатор прогресса

**Что делает.** В настройках видна строка «Последнее действие». Во время любого входящего переноса файлов справа внизу появляется окно с прогрессом, скоростью и оставшимся временем.

**Как устроено.** `ClipboardSyncService.LastActivity` / событие `Activity`: строка «HH:mm текст» — «Отправлено: … → N устр.», «Получено с …», «Не отправлено: …», «Вставка: загрузка … с …», «Приём с …». Описания: «текст (размер)», «изображение (размер)», «данные (размер)», «файлов: N, папок: M (размер)». Прогресс: статический `Files/TransferReports.cs` собирает `FileTransferProgress(OfferId, What, Done, Total, Finished, Ok)` от всех путей (вставка, drag, бросок в окно) и служба переизлучает как `TransferProgress`; `AcceptFilesAsync` поднимает его напрямую. Потребитель — `App.ShowFileTransfer` → `Views/FileTransferWindow` (исключено из захвата экрана; скорость по последним 4 с; скрывается через 2 с после успеха, 5 с после ошибки).

**Ограничения и запасные пути.** Журнала/истории нет. Индикатор скрывается только по `Finished`; его присылает каждый путь при любом исходе: `OfferPrefetcher` (см. выше, включая брошенную вставку — тогда индикатор показывает «Передача не удалась» и скрывается), `EnsureDownloaded` и `AcceptFilesAsync` — при любой ошибке.

**Тесты.** Нет отдельных.

### Безопасность: сводка проверок

- Адресация только (offerId, index) сохранённого предложения; `offerId` — случайный 63-битный.
- `ClipboardCodec.IsSafeEntry` на каждый принятый манифест (offer, drag, viewer, send-files): путь непустой, до 1024 символов, без `/`, `:` и ведущего `\`; каждый сегмент непустой, не `.`/`..`, без запрещённых символов имени, не оканчивается пробелом или точкой; `Size ≥ 0`. Зарезервированные имена (`CON`, `NUL`) и число записей не проверяются.
- `OfferDownloader`: полный путь каждой записи обязан начинаться с корня («Refusing an offer entry outside the target folder»); индекс записи пакета сверяется с манифестом, запись для папки отвергается.
- Отправитель не шлёт больше объявленного размера; получатель считает байты по мере прихода и обрывает передачу на первой записи сверх объявленного (`OfferDownloader`, `OfferPrefetcher`; для чанков — `RemoteFileStream` не принимает чанк длиннее запрошенного), а на `End` сверяет, что файл не короче.
- Пределы: кадр 16 МБ, данные `MaxInlineMegabytes` (включая защиту от zip-бомбы), DROPFILES до 16 МБ, 50 000 записей, 8 предложений, 4 blob-а, 5000 записей для прямой вставки.
- `DragStart` — только от текущего контроллера; `ViewerDrop`/`ViewerCatch` — только при живом потоке и разрешённом управлении; открытие/установка присланного — только после подтверждения пользователем.
- Метки приватности буфера (см. выше).

### Настройки: сводка

Класс `ClipboardSettings` в `src/WinMirroring.Core/Settings/MediaSettings.cs`, свойство `AppSettings.Clipboard`. UI: страница «Буфер обмена» в `src/WinMirroring.Ui/src/settings/Settings.tsx`, ключи `clipboard.enabled`, `clipboard.shareFiles`, `clipboard.dragDrop`, `clipboard.maxInline`, `clipboard.directPaste`.

| Свойство | По умолчанию | На что влияет |
|---|---|---|
| `Enabled` | `true` | отправка и приём данных и файловых предложений |
| `ShareFiles` | `true` | отправка и приём файловых предложений из буфера |
| `MaxInlineMegabytes` | `64` | бюджет чтения и предел приёма текста/картинок (1…1024) |
| `MaxDirectPasteMegabytes` | `64` | до какого размера набор получает `CF_HDROP` (0…4096 в UI) |
| `DragDropEnabled` | `true` | перетаскивание через край и через окно просмотра |

Комментарий к `DragDropEnabled` в коде упоминает только край экрана, но флаг управляет и окном просмотра. Комментарий к `MaxInlineMegabytes` («не отправляются») неточен: отбрасываются отдельные форматы, не влезшие в бюджет.

### Расхождения с документацией и замечания по коду

- `docs/ARCHITECTURE.md` (этап 3): «окном из 3 запросов по 128 КБ» — в коде `RemoteFileStream.Window = 8`, `FileCodec.MaxChunk = 256 КБ` (ниже в том же документе это уже исправлено).
- `docs/ARCHITECTURE.md` §5.3 упоминает проект `WinMirroring.Files` и схему «докачать в кэш, затем положить `CF_HDROP`»; проекта нет, `CF_HDROP` объявляется заранее и скачивается при запросе.
- README: «кэш чистится через сутки» — чистка только при запуске приложения. «Помнит восемь последних наборов скопированных файлов» — в те же восемь входят предложения от перетаскиваний. README называет вкладку «Буфер и звук» (есть в `Views/MainWindow.xaml`), в React-настройках страница называется «Буфер обмена».
- `ComStream` не узнаёт, что Проводник отпустил поток, и вложенный поток не закрывает; `OfferPrefetcher` поэтому не полагается на закрытие потоков и завершается по простою (см. его раздел). На двух устройствах с настоящим Проводником это не проверялось — только тестами с источником в памяти.
- Усечение предложения на 50 000 записей и пропуск недоступных файлов происходят молча.
