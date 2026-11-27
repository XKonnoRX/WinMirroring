# Трансляция экрана: захват, кодирование, доставка (WinMirroring.Screen)

> Часть справочника [docs/FEATURES.md](../FEATURES.md). Составлено по коду версии 1.0.75; источник правды — код.

Область: `src/WinMirroring.Screen/{Capture,Encoding,Transport,Protocol,Native}` и корневые `ScreenSource.cs`, `SendWindow.cs`, `StreamPacing.cs`, `MediaTimerResolution.cs`. Всё ниже сверено с кодом на коммите 47ed112 (1.0.75). Сторона зрителя (`ScreenViewer`, окно) и `ScreenStreamingService*` описаны в других разделах; здесь они упоминаются только там, где от них зависит источник.

### Конвейер одного потока (`ScreenSource`)

**Что делает.** Один `ScreenSource` — один исходящий поток монитора, окна или виртуального монитора (`StreamKind.Monitor/Window/Display`) одному зрителю. Захват, кодирование и отправка разнесены по трём циклам, чтобы период потока был равен самой долгой из стадий, а не их сумме. Темп задаёт сам захват: цикл блокируется на появлении кадра, а не на таймере.

**Как устроено.** `src/WinMirroring.Screen/ScreenSource.cs`. `Start()` берёт таймер 1 мс (`MediaTimerResolution.Hold`), запускает захват и задачи:
- `LoopAsync` — ждёт кадр (`WgcCapture.WaitForFrameAsync`, тайм-аут `IdleWait` = 100 мс), следит за «замиранием» экрана и хвостом, вызывает `Observe()` (темп, статистика, журнал), применяет ограничение частоты пропуском кадра (кадр, пришедший раньше 0,75 × 1000/fps мс после предыдущего взятого, пропускается; сна нет), затем подаёт кадр кодеру (`TryFeedVideo`) либо строит JPEG-плитки (`BuildJpegMessages`).
- `CollectLoopAsync` — каждые `CollectEvery` = 2 мс (на батарее `CollectOnBattery` = 8 мс) забирает готовые access unit'ы (`H264VideoEncoder.TakePending`) и превращает их в сообщения `VideoFrame`; меряет время «в кодере» (`_fedAt` → сбор).
- `SendLoopAsync` — читает очередь `_outbound` (ограниченный `Channel`, глубина 1, полный канал тормозит захват; кадры не выбрасываются), на каждое сообщение берёт слот `SendWindow` под номером кадра (байты 5..8 сообщения) и отправляет по UDP (`TrySendByUdp`) или по каналу `ChannelId.Screen`.
- `PointerLoopAsync` (если зритель рисует указатель сам), `ForegroundLoopAsync`, по запросу `ThumbsLoopAsync`.

Кодер принадлежит циклу захвата, читается циклом сбора; все обращения под `_encoderGate`. Кодеру не дают отстать: при `encoder.Waiting >= MaxQueuedFrames` (2) кадр не подаётся (запрошенный целый кадр переносится на следующий). `End(reason)` поднимает событие `Ended` один раз; `DisposeAsync` дожидается циклов, освобождает захват, кодеры, конвертер, `VideoLink`, отпускает зажатые инжектором клавиши.

**Настройки.** `ScreenSettings.MaxFps` (60), `VideoBitrateKbps` (12 000), `Quality` (70), `SharpStill` (true), `AllowControl` (true); `PowerSettings.Saving` (OnBattery), `PowerSettings.Fps` (30).

**Сообщения.** Control: `ScreenStreamMessage` (`Describe()`: Kind "monitor"/"window"/"display", Title, MonitorIndex, MonitorCount, ControlAllowed, Minimized, ScalePercent, Codec "h264"/"hevc"/"jpeg", MonitorKey = имя дисплея, MediaKey, `CropTags: true` всегда, `MediaReplies` = есть `VideoLink`). Описание пересылается при смене кодека, сворачивании окна, смене монитора.

**Ограничения и запасные пути.** `IOException/InvalidOperationException/ObjectDisposedException` в любом цикле завершают поток с причиной `connection: …`, прочее — `error`. `_frameNo` увеличивают и цикл захвата (плитки), и цикл сбора (видео) без общей блокировки: одновременно это возможно только когда поток видео получает обрезку меньше 16 px (кадр уходит плитками) — гонка теоретическая, не проверялась.

**Тесты.** `ScreenStreamIntegrationTests` (11 из 12 фактов — все, что относятся к конвейеру, — `[Fact(Explicit = true)]`; обычный только `ExtendWithoutTheDriverIsRefusedWithAReason`), `MemoryTests`, `EncoderPipelineTests`. Классы `DirectPresentTests`, `EncoderPipelineTests`, `GpuDecodingTests`, `GpuEncodingTests`, `HevcTests`, `CatchUpDecodingTests`, `SharedSurfaceTests`, `VideoCodecTests` входят в `HardwareVideoCollection` (без параллельного запуска) и пропускаются (`Assert.SkipUnless/SkipWhen`) там, где нет нужного железа.

### Захват монитора и окна (Windows.Graphics.Capture)

**Что делает.** `WgcCapture` захватывает один монитор или одно окно (окно — даже перекрытое другими). Каждый кадр остаётся текстурой на видеокарте; в память его читают только потребители, которым нужны байты (плитки, процессорный путь, чёткий снимок), и только для реально используемых кадров. Потребитель всегда берёт последний кадр, лишние пропускаются.

**Как устроено.** `src/WinMirroring.Screen/Capture/WgcCapture.cs`. `GraphicsCaptureItem` создаётся через `IGraphicsCaptureItemInterop.CreateForMonitor/CreateForWindow` (`Native/ScreenNative.cs`). Пул `Direct3D11CaptureFramePool.CreateFreeThreaded`, формат `B8G8R8A8UIntNormalized`, 2 буфера. Рамка захвата отключается (`IsBorderRequired = false`) на Windows ≥ 10.0.20348. `OnFrameArrived` → `Keep`: кадр копируется на карте (`CopyResource`) в собственную текстуру `_frame` (ShaderResource|RenderTarget), растёт `_version`, поднимается семафор. При изменении `ContentSize` пул пересоздаётся (`Recreate`), текущий кадр пропускается. API: `WaitForFrameAsync(seen, timeout)`, `TryLatestInfo` (размер и версия без пикселей), `WithLatestFrame(Func<ID3D11Texture2D,bool>)` (текстура под блокировкой), `TryCopyLatest(ref byte[])` (читает через staging-текстуру `ReadBack` один раз на версию), `IsDisposed`, `UsesSharedDevice`, событие `Closed`.

Устройство Direct3D — общее на процесс (`Capture/GraphicsDevice.cs`: `GraphicsDevice.Shared`, D3D11 `BgraSupport|VideoSupport`, уровни 11_1/11_0, `SetMultithreadProtected(true)`, блокировка `ContextGate` вокруг контекста). Только на общем устройстве кадр можно отдать кодеру текстурой. `GraphicsDevice.CreateOwn()` — отдельное устройство (его берёт `GpuFrameReader` зрителя, чтобы декодирование не ждало кодер на том же контексте).

**Ограничения и запасные пути.** Нет общего устройства — `WgcCapture` создаёт своё (только `BgraSupport`), `UsesSharedDevice == false`, всё идёт через память. Закрытый захват на все запросы отвечает «кадра нет» (исправление 1.0.68). `Closed` приводит к `End("window closed")` — тем же текстом и для монитора. Для окна дополнительно каждый оборот цикла проверяется `IsWindow` (WGC не всегда поднимает `Closed`). Исключения в `OnFrameArrived` глотаются с Debug-записью.

**Тесты.** `ScreenStreamIntegrationTests` (Explicit): `ViewerReceivesTheMonitor`, `SwitchingMonitorsUnderAStreamDoesNotEndIt`.

### Каталог мониторов и окон, выбор и переключение монитора

**Что делает.** `DisplayCatalog` перечисляет мониторы и окна, пригодные к показу. Зритель называет монитор именем дисплея (ключ) или индексом; переключение монитора идёт внутри того же потока без его завершения.

**Как устроено.** `Capture/DisplayCatalog.cs`: `Monitors()` — `EnumDisplayMonitors` + `GetMonitorInfoW`; порядок: основной первым, затем слева направо, сверху вниз; `Index` — позиция в этом порядке; `MonitorDescription(Index, Handle, DeviceName, Bounds, IsPrimary)`. `Windows()` — видимые, не свёрнутые, корневые, не tool-окна чужих процессов с заголовком и видимыми границами больше 40×40. `ScreenSource.PickMonitor(index, key)`: по `DeviceName`, затем по `Index`, затем по позиции (clamp). `SwitchMonitor` (только `StreamKind.Monitor`): создаёт новый `WgcCapture`, под `_gate` подменяет захват и монитор, сбрасывает обрезку в целое и `_cropId` в 0, меняет `Title`, запускает новый захват, закрывает старый, требует целый кадр. Цикл замечает смену ссылки и обнуляет `seen`. Границы монитора для пересчёта координат перечитываются не чаще раза в секунду (`CurrentMonitorBoundsLocked`).

**Сообщения.** `ScreenRequestMessage(StreamId, MonitorIndex, MonitorKey, MaxFps)` — повторный запрос с тем же StreamId означает переключение; ответ — новый `ScreenStreamMessage`.

**Ограничения и запасные пути.** Кадр, попавший под закрытие старого захвата, отбрасывается (`capture.IsDisposed`), это не считается отказом карты или кодера.

**Тесты.** `MonitorChoiceTests`; `ScreenStreamIntegrationTests.SwitchingMonitorsUnderAStreamDoesNotEndIt` (Explicit).

### Указатель отдельно от картинки

**Что делает.** Если зритель объявил `DeviceCapabilitiesMessage.DrawsPointer`, указатель не впечатывается в кадр, а передаётся отдельно: форма один раз, положение — с частотой мыши. Движение мыши перестаёт порождать видеокадры.

**Как устроено.** `WgcCapture.CaptureCursor = !sendsPointer`. `PointerLoopAsync` каждые `PointerEvery` = 8 мс (на батарее 16 мс) читает `CursorReader.TryRead` (`GetCursorInfo`: хэндл формы, позиция, показан ли), переводит точку экрана в доли передаваемой (обрезанной) картинки (`MapFromScreen`) и шлёт `Cursor`, если изменились видимость, форма или позиция не меньше чем на 0,5 px кадра. Форма рисуется `CursorReader.TryDraw` (`Capture/CursorReader.cs`): `DrawIconEx` дважды — на чёрном и на белом, — разность даёт альфу, результат — premultiplied BGRA с hotspot; размер до `ScreenCodec.MaxCursorSize` = 256. Идентификаторы форм — по хэндлу, словарь до `MaxShapes` = 48, затем очищается. `shapeId == 0` — форму нарисовать не удалось, зритель рисует свою.

**Сообщения.** `Cursor` (11), `CursorShape` (12) по каналу Screen (TCP), вне окна отправки.

**Ограничения и запасные пути.** Старый зритель (без `DrawsPointer`) получает указатель в кадре. Вне передаваемой части указатель помечается невидимым.

**Тесты.** `PointerTests`.

### Размер выдачи: кадр в размере показа

**Что делает.** Зритель сообщает, в каком размере реально рисует картинку; источник кодирует в этом размере, а не в размере захвата.

**Как устроено.** `OnViewerSize` (clamp до 7680×4320) → `FitOutput(crop, wanted, canScale)`: масштаб = min по осям; при масштабе ≥ 0,95, размере меньше 16 px или неизвестном размере — исходный размер обрезки; иначе чётные размеры не меньше 16. Никогда не больше обрезки. Смена размера пересоздаёт кодер (ключевой кадр). Уменьшает либо видеокарта при преобразовании (`GpuNv12Converter`, dest rect), либо процессор (`BgraScaler`).

**Сообщения.** `Size` (10), зритель → источник.

**Ограничения.** В режиме плиток масштабирования нет (плитки в размере обрезки).

**Тесты.** `OutputSizeTests`; `ScreenStreamIntegrationTests.ThePictureIsSentAtTheSizeTheViewerSaysItShowsIt` (Explicit).

### Преобразование цвета и масштаб на видеокарте (`GpuNv12Converter`)

**Что делает.** Превращает захваченный BGRA-кадр в NV12 для кодера прямо на карте, заодно обрезая и масштабируя; кадр не проходит через память. Используется только если карта даёт те же цвета, что процессорный `Nv12Converter` (BT.601, studio swing) — это соглашение обоих концов потока.

**Как устроено.** `Encoding/GpuNv12Converter.cs`, D3D11 Video Processor на общем устройстве. `NextTarget(w,h)` выдаёт по кругу одну из `TargetCount` = 4 NV12-текстур (кодер читает текстуру некоторое время после подачи). `TryConvert(source, crop, target)`: процессор создаётся под пару размеров (вход — весь кадр, выход — целевой размер; 60/1, `PlaybackNormal`), source rect = обрезка, dest rect = выход, `VideoProcessorBlt` под `ContextGate`. Перед использованием `VideoProcessorRules.NothingOfItsOwn` (`Encoding/VideoProcessorRules.cs`): выключены авто-обработка и фильтры Brightness/Contrast/Hue/Saturation/NoiseReduction/EdgeEnhancement — одинаково на обеих сторонах.

Цветовые режимы (`ColourMode`), пробуются в порядке `Named`, `Legacy`, `NamedStudio`, `LegacyStudio`:
- `Named`/`NamedStudio` — `ID3D11VideoContext1`: вход `RgbFullG22NoneP709`, выход `YcbcrFullG22LeftP601` / `YcbcrStudioG22LeftP601`;
- `Legacy`/`LegacyStudio` — `VideoProcessorColorSpace` с `RGB_Range = 0`, `YCbCr_Matrix = 0` (BT.601), `Nominal_Range` 2 / 1.

Цветовая проба (`Probe`): картинка 128×128 из полос по 16 строк (чёрный, серый, белый, насыщенные цвета) преобразуется картой и сравнивается с `Nv12Converter.BgraCropToNv12` по средним строкам полос; допуск ≤ 6 на компоненту. Первый совпавший режим кэшируется статически на процесс.

**Ограничения и запасные пути.** Нет общего устройства, ни один режим не прошёл, не создалась текстура или `Blt` не удался — `GiveUpOnGpu()`: до конца потока кадр читается в память и преобразуется процессором (в журнале «is converted by the processor»). Результат пробы (в том числе отрицательный) живёт до перезапуска процесса. Отказ из-за закрытого при переключении монитора захвата отказом карты не считается.

**Тесты.** `GpuEncodingTests` (один факт Explicit: `WhatTheWholePathDoesToEachColour`), `DisplayColourProbe` (Explicit).

### Процессорный путь: `Nv12Converter` и `BgraScaler`

**Что делает.** Запасной путь преобразования и масштабирования в памяти и одновременно эталон цвета для проб обеих карт.

**Как устроено.** `Encoding/Nv12Converter.cs`: целочисленный BT.601 studio swing. BGRA→NV12: `Y = ((66R+129G+25B+128)>>8)+16`, U/V по усреднённому блоку 2×2; параллельно по парам строк при высоте ≥ 64. NV12→BGRA (`Nv12ToBgra`, параметр `uvOffset` для кадра декодера с кодированной высотой, кратной 16; `RowsIn`) — `Vector128`, по 4 пикселя, параллельно. `Even()` округляет вниз до чётного, `Nv12Size`.
`Encoding/BgraScaler.cs`: `Scale(source, sourceWidth, crop, target, w, h)` — до уменьшения вдвое билинейная интерполяция (веса /256), ниже половины (по любой оси) — усреднение покрываемой области; параллельно от 64 строк; альфа = 255.

**Тесты.** `Nv12ConverterTests`, `BgraScalerTests` (один Explicit: `HowLongAFullScreenTakesToMakeSmaller`), `ConversionCostProbe` (Explicit).

### Аппаратный кодер H.264/HEVC (`H264VideoEncoder`)

**Что делает.** Аппаратный кодер поверх асинхронного Media Foundation Transform: на входе NV12 (байты или текстура), на выходе access unit'ы Annex-B. Один класс обслуживает оба формата (`VideoFormat.H264`/`Hevc`).

**Как устроено.** `Encoding/H264VideoEncoder.cs`, `Encoding/VideoInterop.cs`. `TryCreate(width, height, fps, bitrateKbps, logger, format)`: размеры чётные и ≥ 16; `MFTEnumEx(VideoEncoder, Hardware|SortAndFilter, NV12 → H264/HEVC)`, берётся первый кандидат, который удалось настроить. MFT получает `IMFDXGIDeviceManager`: предпочтительно на `GraphicsDevice.Shared` (тогда `UsesSharedDevice` и возможна подача текстуры), иначе на собственном устройстве, иначе без менеджера. Настройки: `TransformAsyncUnlock`, `MF_LOW_LATENCY`; выходной тип — `AvgBitrate` (clamp 500–100 000 кбит/с), размер, `fps/1`, PAR 1:1, progressive, профиль Main (H.264: 77; HEVC Main 4:2:0 8 бит: 1); через `ICodecAPI` (best-effort): low latency, CBR, 0 B-кадров, `AVEncMPVGOPSize = clamp(fps × 600, 1, 36 000)` — ключевых кадров по расписанию фактически нет, средний битрейт.

Поток-насос `h264-encoder`: `GetEvent(NO_WAIT)`, на `TransformNeedInput` подаёт из очереди `_inputs`, на `TransformHaveOutput` — `ProcessOutput` (сэмпл либо от MFT, либо одолженный `MfOutputSample`), байты копируются в `VideoAccessUnit(Data, Keyframe)`; без событий — `Sleep(1)`. Ключевой кадр распознаётся по NAL (`VideoInterop.ContainsKeyframe`): H.264 — типы 5 и 7; HEVC — 16–21, 32, 33.

API потока: `Feed(bgra, frameWidth, crop, forceKeyframe)` (CPU-преобразование в `_nv12`), `FeedTexture(nv12Texture, forceKeyframe)` (`MFCreateDXGISurfaceBuffer`, без копии), `HasPendingOutput`, `TakePending()`, `Waiting`, `Retune(fps, bitrateKbps)`. `Encode`/`EncodeTexture` (подать и ждать до `OutputWait` = 35 мс) используют только тесты и замеры. Время сэмплов накапливается (`_sampleTimeHns += 10^7/fps`). Ключевой кадр по запросу — `AVEncVideoForceKeyFrame`.

Битрейт и частота: `ScreenSource.ScaleBitrate(w,h) = clamp(VideoBitrateKbps, 500, 100 000) × clamp(sqrt(w·h / (1920·1080)), 0,25, 1) × StreamPacing.Share`; `Retune` вызывается на каждом обороте цикла, но в драйвер битрейт уходит только при изменении не меньше чем на 20 %. Частота кодера = `StreamPacing.Fps` = clamp(эффективный максимум, 1, 60).

**Настройки.** `VideoBitrateKbps` (12 000; действует и для HEVC), `MaxFps` (60).

**Ограничения и запасные пути.** Картинка больше 1920×1080 получает тот же битрейт, что 1080p (множитель ограничен 1). Нижняя граница в кодере 500 кбит/с, даже если расчёт дал меньше. Без `ICodecAPI` запрос ключевого кадра и настройки молча игнорируются — что тогда делает конкретный драйвер, не выяснено. Смена размера или формата = новый кодер. Исключение при подаче → «H.264 encode failed», поток переходит на плитки.

**Тесты.** `EncoderPipelineTests` (Explicit: `HowLongTheEncoderHoldsAFrame`), `VideoCodecTests`, `HevcTests`, `GpuEncodingTests`, `FrameSizeProbe` (Explicit), `MemoryTests`, `MfOutputSampleTests`.

### Выбор формата и отступление H.264 ← HEVC → JPEG-плитки

**Что делает.** Формат выбирается до старта по настройке и объявленным возможностям обеих сторон, а во время потока ступенчато снижается при отказах: HEVC → H.264 → плитки.

**Как устроено.** Возможности — `Encoding/VideoCodecSupport.cs`, пробы реальным созданием MFT 640×480, `Lazy`, кэш на процесс: `CanEncodeH264`, `CanDecodeH264`, `CanEncodeHevc`, `CanDecodeHevc` (HEVC «декодируется» только если декодер работает на карте: `UsesGraphicsCard`). Они уходят в `DeviceCapabilitiesMessage`. `ScreenStreamingService.NegotiateVideo`: видео, если `VideoCodec != Jpeg`, есть свой кодер H.264 и зритель декодирует H.264 (`Auto`, `H264`, `Hevc` здесь равнозначны). `WithFormat`: `PreferHevc` = настройка `Hevc` + свой кодер HEVC + `CanDecodeHevc` у зрителя.

Во время потока (`ScreenSource`):
- кодер HEVC не создался → `_hevcFailed`, сразу H.264, новое описание потока;
- зритель прислал `ScreenCodecFallbackMessage` (`FallBackToTiles`) при HEVC → H.264; при H.264 → `_videoFailed`, плитки, конвертер освобождается;
- кодер H.264 не создался или подача бросила исключение → плитки до конца потока;
- обрезка меньше 16 px по стороне → этот кадр плитками.
Зритель узнаёт формат по первым байтам ключевого кадра (`VideoInterop.Sniff`: NAL-заголовок 0x40/0x42/0x44/0x46 + 0x01 → HEVC), а не по описанию.

**Настройки.** `ScreenSettings.VideoCodec` (Auto; Auto/Jpeg/H264/Hevc) — читает `ScreenStreamingService`.

**Сообщения.** `DeviceCapabilitiesMessage`, `ScreenStreamMessage.Codec`, `ScreenCodecFallbackMessage`.

**Ограничения.** Пробы кэшируются: установка HEVC Video Extensions требует перезапуска. Возврата на более высокий формат в рамках потока нет. По записи в ARCHITECTURE отступление HEVC → H.264 по жалобе зрителя вживую не проверено.

**Тесты.** `HevcTests`, `VideoCodecTests`; `ScreenStreamIntegrationTests.ViewerReceivesTheMonitorAsVideo/AsHevc` (Explicit).

### JPEG-плитки (`TileEncoder`/`TileDecoder`)

**Что делает.** Запасной и явно выбираемый кодек: картинка (обрезка) делится на плитки 128×128, с прошлым отправленным сравнивается побайтно, в JPEG кодируются только изменившиеся. Неподвижный экран ничего не стоит.

**Как устроено.** `Encoding/TileCodec.cs`. `TileEncoder.Encode(frame, frameWidth, frameHeight, crop, forceAll)`; смена размера обрезки = все плитки; кодирование WIC (24bpp BGR) параллельно. Качество: `clamp(Quality, 30, 95)`, далее `30 + (q − 30) × Share` (канал не тянет — плитки грубее). `ScreenSource.Split` режет набор плиток на сообщения `Frame` до `PartBytes` = 512 КБ, каждое со своим номером кадра и слотом окна. `TileDecoder.DecodeInto` пишет плитку в BGRA-буфер зрителя (плитка вне картинки — `InvalidDataException`).

**Настройки.** `Quality` (70).

**Ограничения.** Плитки идут только по TCP; без имён обрезки, без масштаба под зрителя, без чёткого снимка. `VideoLink` для потока, начатого как JPEG, не создаётся.

**Тесты.** `TileAndCodecTests`, `MemoryTests`.

### Декодер и чтение кадра с карты (используются зрителем)

**Что делает.** `H264VideoDecoder` декодирует access unit'ы H.264/HEVC; где возможно — на видеокарте, оставляя кадр на ней. `GpuFrameReader` превращает NV12 декодера в BGRA на карте; `GpuSurface` — общая текстура, из которой рисует окно; `Direct3D9Bridge` открывает её для WPF `D3DImage`.

**Как устроено.**
- `Encoding/H264VideoDecoder.cs`: синхронный MFT (`MFTEnumEx(VideoDecoder, SyncMft|SortAndFilter)`), `MF_LOW_LATENCY`. Если MFT `D3D11Aware` и есть `GpuFrameReader`, ему даётся device manager на устройстве ридера. Выход NV12 (`SelectNv12Output`); размер картинки берётся из сообщений (`Expect(w,h)`), а не из типа MFT (тот даёт кодированный размер, например 1088 строк). `DecodeInto(accessUnit, ref bgra, out w, out h, read = true, onto = null)`: к каждому access unit'у дописывается разделитель (H.264: `00 00 00 01 09 10`; HEVC: `00 00 00 01 46 01 50`), иначе картинка вышла бы на кадр позже; до 3 попыток `ProcessOutput` с обработкой `MF_E_TRANSFORM_STREAM_CHANGE`. `read: false` — декодировать, но не превращать в пиксели (догоняющий кадр). Три выхода: в `GpuSurface` (`LastWasLeftOnCard`), чтение с карты в `bgra` (`GpuFrameReader.TryRead`), в памяти (`IMF2DBuffer.ContiguousCopyTo` + `Nv12ToBgra`). `GraphicsCardFailed` — после `GiveUpOnCardAfter` = 4 отказов подряд либо сразу, если сэмпл оказался не DXGI; зритель тогда пересоздаёт декодер с `useGraphicsCard: false`.
- `Encoding/GpuFrameReader.cs`: собственное устройство (`GraphicsDevice.CreateOwn`). Режимы `Reading` в порядке пробы: `NamedStudio`, `NamedFull`, `LegacyStudio`, `LegacyFull`; проба — NV12 128×144 (16 «лишних» строк пурпурного цвета под картинкой, как у кодированного размера), сравнение с `Nv12Converter.Nv12ToBgra`, допуск ≤ 6, статический кэш. `TryRead(picture, arraySlice, w, h, bgra)`, `CreateSurface(w,h)` (BGRA8, RenderTarget|ShaderResource, `ResourceOptionFlags.Shared`, legacy shared handle), `TryConvertOnto(picture, slice, surface)` + `Flush`.
- `Encoding/GpuSurface.cs`: `Handle`, `Width`, `Height`, `IsDisposed`.
- `Encoding/Direct3D9Bridge.cs`: `IDirect3DDevice9Ex` (окно рабочего стола, 1×1, Multithreaded); `Open(GpuSurface)` → указатель поверхности для `D3DImage.SetBackBuffer`; кэш по объекту (не по хэндлу), до `MostOpen` = 8; `Read()` — для тестов.
- `Encoding/MfOutputSample.cs`: один переиспользуемый выходной сэмпл на всю жизнь трансформа (лечит утечку буфера на каждый кадр и двойное освобождение обёртки из `ProcessOutput`).

**Настройки.** `ScreenSettings.DirectPresent` (true) — читает зритель.

**Ограничения.** HEVC в памяти даёт неверную картинку (комментарий: «measured»), поэтому объявляется только при декодировании на карте. На Intel Iris Xe путь «кадр на карте» по ARCHITECTURE не проверен.

**Тесты.** `GpuDecodingTests` (два Explicit), `SharedSurfaceTests`, `DirectPresentTests` (Explicit), `CatchUpDecodingTests` (в `LatencyTests.cs`), `VideoCodecTests`, `HevcTests`, `MfOutputSampleTests`, `MemoryTests`.

### Когда отправляется целый (ключевой) кадр

**Что делает.** Ключевые кадры не идут по расписанию (GOP ≈ 10 минут при 60 fps), а запрашиваются флагом `_forceAll`.

**Как устроено.** Флаг ставят: старт потока; новый кодер (размер/формат); `OnCrop`; `OnViewerSize` (при изменении); `SwitchMonitor`; `OnUdpReady` (путь UDP подтверждён); отказ UDP-пути в `SendLoopAsync`; `OnRefreshRequested` — сообщение `Refresh` зрителя или сторож хвоста, не чаще `RefreshAtMost` = 250 мс; `FallBackToTiles`; «замирание» — экран не менялся `SettleAfter` = 180 мс (последний кадр движения самый размытый; один целый кадр на паузу). Запрошенный, но пропущенный из-за занятого кодера кадр переносится. В режиме плиток флаг означает «все плитки».

**Сообщения.** `Refresh` (14).

### Чёткий снимок замершего экрана (`StillCodec`)

**Что делает.** Через 0,6 с неподвижности экран досылается одним JPEG 4:4:4 с полным цветом каждого пикселя; зритель кладёт его поверх видео до следующего видеокадра.

**Как устроено.** Условия в `LoopAsync` (проверяются на тайм-ауте ожидания кадра, шаг 100 мс): `SharpStill`, поток видео и не на плитках, уже отправлен «замерший» ключевой кадр (`_settled`), `!StreamPacing.HeldBack`, прошло `StillAfter` = 600 мс, за эту паузу снимок ещё не слали. `SendStillAsync` (отдельная задача): `TryCopyLatest` (версия должна совпасть), та же чётная обрезка и тот же `FitOutput`-размер, что у видео, `BgraScaler.Scale`, `StillCodec.Encode` (`Encoding/StillCodec.cs`: WIC JPEG, `ImageQuality` 0,93, `JpegYCrCbSubsampling` = 3); затем повторная проверка, что версия захвата и `_frameNo` не изменились; отправка `ScreenCodec.EncodeStill(streamId, afterFrameNo = _frameNo, w, h, cropId, jpeg)`. `StillCodec.Decode` → BGRA, сторона до 16 384.

**Настройки.** `SharpStill` (true).

**Сообщения.** `Still` (15) — всегда по TCP-каналу Screen, вне окна отправки, без подтверждения.

**Ограничения.** Снимок всегда делается процессором (чтение кадра в память), даже если видео идёт целиком на карте. Если экран не замирает (анимированные обои), снимка нет. Если снимок отменён из-за движения, новый будет только после следующей паузы.

**Тесты.** `StillCodecTests`; `ScreenStreamIntegrationTests.AScreenThatStandsStillIsFollowedByASharpPictureOfIt` (Explicit).

### Протокол канала Screen (`ScreenCodec`)

**Что делает.** Двоичные сообщения канала `ChannelId.Screen`, little-endian: байт 0 — `ScreenMessageType`, байты 1..4 — `streamId`.

**Как устроено.** `Protocol/ScreenCodec.cs`. Смещения — от начала сообщения.

| Тип | Напр. | Длина | Поля после заголовка | Назначение |
|---|---|---|---|---|
| `Frame` = 1 | И→З | 15 + плитки | u32 frameNo @5, u16 width @9, u16 height @11, u16 tileCount @13; плитка: u16 x, y, w, h, i32 length, JPEG | изменившиеся JPEG-плитки |
| `Ack` = 2 | З→И | 13 (9 или 11 у старых) | u32 frameNo @5, u16 viewerMs @9, u16 paintMs @11 | кадр обработан; отсутствующие поля читаются как −1 |
| `Pointer` = 3 | З→И | 17 | u8 action @5 (`PointerAction`: Move 1, Down 2, Up 3, Wheel 4, HorizontalWheel 5), u8 button @6, f32 x @7, f32 y @11, i16 wheel @15 | указатель в долях передаваемой картинки; допуск −8…9 (`PositionReach` = 8) |
| `Key` = 4 | З→И | 10 | u16 vk @5, u16 scan @7, u8 flags @9 (bit0 up, bit1 extended) | клавиша |
| `Crop` = 5 | З→И | 22 (21 у старых) | f32 x @5, y @9, w @13, h @17, u8 id @21 | передавать только эту часть; `Normalize`: clamp 0..1, сторона меньше 0,02 или мусор → целое |
| `Touch` = 6 | З→И | 15 | u8 action @5 (1 down, 2 move, 3 up, 4 cancel), u8 contact @6, f32 x @7, f32 y @11 | касание |
| `Text` = 7 | З→И | 5 + 2n | UTF-16 LE, n ≤ `MaxTextLength` = 4096 | текст без клавиши |
| `Window` = 8 | З→И | 6 | u8 command @5 (`WindowCommand.Restore` = 1) | команда окну-источнику |
| `VideoFrame` = 9 | И→З | 16 + AU | u32 frameNo @5, u16 width @9, u16 height @11, u8 flags @13 (bit0 keyframe), u8 cropId @14, u8 резерв @15, access unit Annex-B | один кадр H.264/HEVC |
| `Size` = 10 | З→И | 9 | u16 width @5, u16 height @7 (0 — «как есть») | размер показа |
| `Cursor` = 11 | И→З | 18 | u32 shapeId @5, f32 x @9, f32 y @13, u8 flags @17 (bit0 visible) | положение указателя |
| `CursorShape` = 12 | И→З | 17 + w·h·4 | u32 shapeId @5, u16 w @9, u16 h @11, i16 hotspotX @13, i16 hotspotY @15, premultiplied BGRA | форма указателя, до 256×256 |
| `UdpReady` = 13 | З→И | 5 | — | UDP-пробы доходят |
| `Refresh` = 14 | З→И | 5 | — | нужен целый кадр |
| `Still` = 15 | И→З | 16 + JPEG | u32 afterFrameNo @5, u16 width @9, u16 height @11, u8 cropId @13, резерв @14..15 | чёткий снимок |

Вспомогательное: `TryReadHeader`, `TryReadVideoFrame` (без копии AU — для UDP), `VideoCropId`, `EncodeRequest` (сообщения из одного заголовка), `ToPixels`.

**Ограничения.** Размеры — u16. XML-комментарий класса устарел относительно кода (см. расхождения).

**Тесты.** `TileAndCodecTests`, `VideoCodecTests`, `PointerTests`, `OutputSizeTests`, `ViewPartTests`, `StillCodecTests`.

### Доставка по UDP: `VideoLink`

**Что делает.** Видеокадры уходят датаграммами, чтобы потерянный сегмент TCP не задерживал следующий кадр. Путь никогда не предполагается рабочим: пока зритель не подтвердил пробы, кадры идут по соединению; замолчавший путь оставляется и позже пробуется снова.

**Как устроено.** `Transport/VideoLink.cs`. Создаётся в конструкторе `ScreenSource`, только для видеопотока и только если у узла есть медиасокет (`node.MediaPort != 0`); ключ AES-256-GCM (`MediaCipher.GenerateKey`, 32 байта) передаётся в `ScreenStreamMessage.MediaKey` внутри TLS; сокету ставится приоритет (`node.PrioritizeMedia`). Виды датаграмм: `FrameKind` = 2, `ProbeKind` = 3 (звук — 1; ответы зрителя — 4 и 5). Пакет `MediaCipher`: magic 0xA7, kind, streamId (4, BE), sequence (8, BE), шифртекст, тег 16 — накладные 30 байт.

Состояния `Path`:
- `Probing` — `ProbeIfDue()` (вызывается из цикла отправки, то есть только вместе с кадрами) шлёт пустую пробу не чаще `ProbeEvery` = 120 мс; после `ProbesBeforeResting` = 16 без ответа → `Resting`.
- `Carrying` — после `Confirm()` (сообщение `UdpReady`); первый кадр на новом пути целый. `TrySend` режет кадр на датаграммы с чётностью и запоминает его. Отказ: не меньше `UnansweredBeforeGivingUp` = 3 неотвеченных кадров и больше `SilenceBeforeGivingUp` = 1 с с первого из них → `Resting`, `TrySend` возвращает false, кадр уходит по TCP, следующий — целый.
- `Resting` — отдых `FirstRest` = 10 с, удваивается до `LongestRest` = 2 мин; если путь до отказа проработал дольше `ProvenAfter` = 60 с, отдых снова 10 с. Потом снова `Probing`.

Досылка: отправленные кадры хранятся (`MostKept` = 64, `MostKeptBytes` = 8 МиБ, `KeptFor` = 1,5 с); `Resend(frameNo, pieces)` шлёт названные куски либо весь кадр с чётностью (пустой список), не больше `MostResendsOfOne` = 3 раз на кадр. `Receive(packet, acks)` открывает ответ зрителя отдельным экземпляром шифра и отвечает на запрос досылки прямо из потока приёма; отправка под `_sendGate`. Счётчики `Datagrams`, `Resent`.

**Сообщения.** `UdpReady` (13); `ScreenStreamMessage.MediaKey`, `MediaReplies`.

**Ограничения и запасные пути.** `TrySend` отказывает (кадр идёт по TCP) для кадра больше `MaxPictureBytes` или вне состояния `Carrying`. Плитки, указатель и чёткий снимок по UDP не ходят. После перехода потока на плитки состояние `Carrying` не сбрасывается (см. расхождения). По ARCHITECTURE досылка на настоящем Wi-Fi не проверена.

**Тесты.** `LatencyTests`, `ResendTests`; `ScreenStreamIntegrationTests.PicturesGetThroughALinkThatLosesOneDatagramInTwelve` (Explicit).

### Формат датаграммы и чётность (`VideoDatagram`)

**Что делает.** Режет один закодированный кадр на датаграммы, каждая помещается в один IP-пакет; на каждый блок кусков — датаграмма чётности (XOR), которая восстанавливает один потерянный кусок блока без запросов.

**Как устроено.** `Transport/VideoDatagram.cs`. Заголовок `HeaderSize` = 18 байт, LE: frameNo(4) total(4) width(2) height(2) count(2) index(2) flags(1, bit0 keyframe) tag(1, имя обрезки). `SafeDatagramBytes` = 1200; `PayloadSize` = 1200 − 30 − 18 = 1152. `ParityBlock` = 32 куска на чётность; индекс чётности блока b = `ParityIndex` (0xFFFE) − b. `MaxFragments` = 4096, `MaxPictureBytes` = 4 МиБ. `Pieces(total)` = куски + по чётности на блок; кадр из одного куска идёт без чётности. Порядок: сначала все куски, затем чётности. `TryReadHeader` отвергает: total вне 1..4 МиБ, count вне 1..4096, индекс вне кадра и не чётность существующего блока, `Fragments(total) != count`, длину полезной нагрузки, не равную ожидаемой.

**Тесты.** `VideoDatagramTests`, `ViewPartTests`, `ResendTests`, `FrameSizeProbe` (Explicit).

### Сборка кадров у зрителя (`VideoReassembly`)

**Что делает.** Собирает кадры из датаграмм и отдаёт строго по порядку номеров; восстанавливает одиночную потерю по чётности; решает, когда кадр уже не придёт; при поддержке источником — сначала просит недостающее.

**Как устроено.** `Transport/VideoReassembly.cs`, не потокобезопасен, часы внедряются. `Add(header, payload)`: первый увиденный номер — точка отсчёта; `Repair` — один недостающий кусок на блок; `Flush` отдаёт целые кадры по порядку. Без досылки (`askAgain == null`): кадр бросается, когда начали приходить `Lead` = 2 более поздних или прошло `Reorder` = 8 мс с появления более позднего. С досылкой: запрос, когда пришла чётность последнего блока, а кадр не цел; когда пришёл следующий кадр; из `Tick()`, если кадр не получал кусков `QuietBeforeAsking` = 5 мс; повтор через `AskTwiceAfter` = 12 мс, не больше двух запросов; бросается при `LeadWhenAsking` = 4 поздних кадрах или через `RepairWait` = 40 мс; одинокий неполный кадр — через `AloneFor` = 80 мс. `giveUp(frameNo)` — зритель подтверждает номер и просит `Refresh`. `Passed(frameNo)` — кадр пришёл по соединению, очередь сдвигается. Счётчики `Delivered`, `Repaired`, `Lost`, `Asked`; `Waiting`.

**Тесты.** `VideoDatagramTests`, `ResendTests`, `LatencyTests`, `ViewPartTests`.

### Ответы зрителя датаграммами (`ViewerReplies`)

**Что делает.** Подтверждения и запросы досылки идут и по UDP, чтобы не вставать за потерянным сегментом TCP. Ни то ни другое не обязано дойти: подтверждения дублируются по соединению.

**Как устроено.** `Transport/ViewerReplies.cs`. `AckKind` = 4: count(1), затем до `AcksPerDatagram` = 4 записей по 8 байт: frame(4) held(2) painted(2) — текущее подтверждение и три предыдущих. `ResendKind` = 5: frame(4) count(2), номера кусков по 2 байта; count = 0 — весь кадр; максимум `MostPiecesAsked` = 96 (больше — запрос целого); `MaxPayload` = 198. Зритель шифрует ключом потока, нумеруя с `ViewerSequenceBase` = 2^63 (nonce = streamId + sequence, половины не пересекаются); источник отбрасывает ответы с номером ниже 2^63. `SendWindow` сопоставляет по номеру кадра, поэтому подтверждение, пришедшее двумя путями, считается один раз.

**Сообщения.** Объявляется `ScreenStreamMessage.MediaReplies`; без него зритель датаграмм не шлёт.

**Тесты.** `ResendTests`; `ScreenStreamIntegrationTests` (Explicit).

### Канал TCP/TLS

**Что делает.** Канал `ChannelId.Screen` мультиплексированной TLS-сессии — путь по умолчанию и запасной.

**Как устроено.** По нему всегда идут: плитки `Frame`, `Cursor`/`CursorShape`, `Still`, все сообщения зрителя (`Ack`, ввод, `Crop`, `Size`, `UdpReady`, `Refresh`), а `VideoFrame` — пока `VideoLink` не в `Carrying` либо когда `TrySend` отказал. Сообщение `VideoFrame` и UDP-кадр несут одни и те же байты (`TrySendByUdp` читает кадр прямо из готового сообщения, массив хранится для досылки). Управляющие сообщения — в канале Control.

### Управление потоком: `SendWindow`

**Что делает.** Ограничивает число неподтверждённых сообщений кадров «в полёте» и измеряет канал. Окно и есть регулятор фактической частоты: поток идёт не быстрее, чем подтверждаются кадры.

**Как устроено.** `SendWindow.cs`; в `ScreenSource`: `MaxInFlight` = 3, `AckGrace` = 250 мс, `LateAbove` = 120 мс. `TakeAsync(frameNo)` берёт слот непосредственно перед записью в сеть; если окно полно и старейший кадр старше grace — он вытесняется (`GivenUp++`), поток не замирает. `OnAck(frameNo, viewerMs, paintMs)`: ищет кадр по номеру, освобождает его и все более ранние; сглаживание 0,75/0,25 для `DelayMs`, `ViewerMs`, `PaintMs`; подтверждение позже `LateAbove` — «позднее». Часы — `Stopwatch`. `Reading` → `LinkReading(DelayMs, ViewerMs, Acks, LateAcks, GivenUp, PaintMs)`; `OldestAgeMs`, `OldestFrame`, `InFlight`. Подтверждение уже вытесненного кадра игнорируется.

**Сообщения.** `Ack` (2) по TCP и `AckKind` по UDP.

**Тесты.** `FlowControlTests`, `LatencyTests`, `ResendTests`.

### Подстройка под канал: `StreamPacing`

**Что делает.** Двигает одну величину — долю настроенного битрейта (`Share`, она же сжимает диапазон качества плиток). Частоту кадров не трогает.

**Как устроено.** `StreamPacing.cs`. `Observe(reading, maxFps, now)` раз в `AdjustEvery` = 3 с, если за окно набралось не меньше `EnoughAcks` = 8 событий: доля поздних = (поздние + вытесненные) / (подтверждения + вытесненные). Больше `TooManyLate` = 0,25 → ×`Down` 0,8; больше `FarTooLate` = 0,5 → ×`FarDown` 0,6; меньше `FewLate` = 0,05 и `Share < 1` → ×`Up` 1,1. Границы: `MinShare` = 1/3 … 1. `HeldBack` = `Share < 0,99`. `Fps` = clamp(maxFps, 1, 60). `Changes` — счётчик реальных изменений.

Эффективный максимум частоты (`ScreenSource.EffectiveMaxFps`): `PowerMode.Fps(MaxFps)` (в режиме экономии min(MaxFps, `PowerSettings.Fps`)), дополнительно min с `ViewerFps` (`ScreenRequestMessage.MaxFps` — зритель на батарее).

**Настройки.** `MaxFps` (60), `PowerSettings.Saving` (OnBattery), `PowerSettings.Fps` (30).

**Тесты.** `FlowControlTests`.

### Сторож хвоста (потеря последнего кадра серии)

**Что делает.** Последний кадр перед паузой, потерянный по UDP, никто не заметит: следующего кадра нет. Источник сам замечает неотвеченный кадр на неподвижном экране.

**Как устроено.** `LoopAsync`, только при `VideoLink.Carries`, когда нового кадра нет: если `SendWindow.OldestAgeMs` > max(`TailResendAfterMs` = 30, 2 × `DelayMs`) и этот кадр ещё не досылался — `VideoLink.Resend(frameNo, [])` (тот же кадр целиком); если > max(`TailLossAfterMs` = 100, 3 × `DelayMs`) — `OnRefreshRequested()` (целый кадр, не чаще раза в 250 мс, пока не придёт ответ).

**Тесты.** Сама логика в `LoopAsync` модульно не покрыта; её опоры — `LatencyTests.TheOldestUnansweredFrameSaysHowLongItHasWaited` (`OldestAgeMs`) и `ResendTests.ThePictureIsNotSentAgainForEver` (предел досылок).

### Обрезка и приближение: «только видимая часть»

**Что делает.** Зритель просит передавать только часть захваченной картинки (выделенная рамкой часть либо видимая при приближении область). Каждый видеокадр возвращает имя запроса, по которому сделан, чтобы зритель рисовал его на своём месте.

**Как устроено.** `OnCrop(crop, id)` сохраняет нормированный прямоугольник и `_cropId`, требует целый кадр. В цикле `ScreenCodec.ToPixels` → пиксели; для видео размеры обрезки округляются до чётных. Имя кадра фиксируется в момент подачи кодеру (`_fedTags`, очередь под `_encoderGate`) и снимается при сборе: один кадр на входе — один на выходе; если имён больше, чем `encoder.Waiting + 2`, лишние старые отбрасываются. Имя едет в байте 14 `VideoFrame`, в байте `tag` заголовка датаграммы и в байте 13 `Still`. Смена обрезки того же размера кодер не пересоздаёт. Битрейт меньшей картинки падает как корень из площади (`ScaleBitrate`), а не как площадь. Пересчёт координат: `MapToScreen` (доли обрезки → пиксель экрана, с прижатием к границам монитора/окна), `MapFromScreen` (обратно, для указателя), `TryMapToScreen`.

**Сообщения.** `Crop` (5) с id; `ScreenStreamMessage.CropTags`.

**Ограничения.** Плитки имён не несут. Окно зрителя включает запрос частей только при `Info.Codec == "h264"` (`src/WinMirroring.App/Views/ScreenViewerWindow.Detail.cs`, `PartsPossible`) — на потоке HEVC приближение увеличивает целую картинку (см. расхождения). По ARCHITECTURE отрисовка части в окне на устройстве не проверена.

**Тесты.** `ViewPartTests`, `TileAndCodecTests`, `PointerTests`; `ScreenStreamIntegrationTests.APartOfTheScreenComesBackUnderTheNameItWasAskedForBy` (Explicit).

### Показатели, строка журнала и отметка «подвисло»

**Что делает.** Источник раз в секунду сообщает зрителю то, что измерить можно только здесь, раз в 10 секунд пишет строку потока в журнал, а по отметке зрителя — немедленно.

**Как устроено.** `Observe()` → `TellTheViewer` (`StatsEvery` = 1 с): `ScreenStatsMessage(StreamId, AckMs, ViewerMs, EncoderMs, Kbps, Fps, Udp, SharePercent, GaveUp, Resent)`; Kbps и Fps — по сообщениям, взявшим слот окна; EncoderMs — среднее время от подачи кадра до сбора. Журнал (`ReportEvery` = 10 с, Information): кодек, путь (UDP с числом датаграмм и досылок либо «the connection»), сообщений/с, кбит/с, доля битрейта и «held back», fps, число изменений битрейта, мс в кодере, задержка подтверждения с долями декодирования и отрисовки, поздние/всего, вытесненные, в полёте. `OnMark(note)` (заметка до 200 символов): Warning `HITCH marked by the viewer …` с текущими замерами и внеочередная строка потока. Отдельные Information-записи: поток идёт как HEVC; кодируется на карте; преобразуется процессором; отправляется в размере показа; первый чёткий снимок.

**Настройки.** `ScreenSettings.ShowStats` (false) читает зритель; источник шлёт показатели всегда.

**Сообщения.** `ScreenStatsMessage`, `ScreenMarkMessage`.

**Тесты.** `ScreenStreamIntegrationTests.TheViewerIsToldHowTheStreamIsDoingAndCanMarkAHitch` (Explicit).

### Активная программа на источнике

**Что делает.** Сообщает зрителю имя процесса окна на переднем плане, чтобы тот показывал набор кнопок под программу.

**Как устроено.** `ForegroundLoopAsync`, период `ForegroundEvery` = 400 мс: `GetForegroundWindow` (для потока окна — само окно) → `GetWindowThreadProcessId` → `Process.ProcessName`; отправка при старте и при смене имени (сравнение без учёта регистра). Недоступный процесс — пустое имя, сообщение не шлётся.

**Сообщения.** `ScreenForegroundMessage(StreamId, Process)`.

**Тесты.** Отдельных в `WinMirroring.Screen.Tests` не найдено.

### Миниатюры остальных мониторов (`MonitorThumbnail`)

**Что делает.** Пока у зрителя открыто меню, источник шлёт маленькие картинки своих остальных мониторов.

**Как устроено.** `OnThumbsWanted(on)` (только `StreamKind.Monitor`) продлевает срок на `ThumbsWantedFor` = 6 с и запускает `ThumbsLoopAsync`: каждые `ThumbsEvery` = 1,5 с для каждого монитора, кроме показываемого, `MonitorThumbnail.Capture(bounds)` (`Capture/MonitorThumbnail.cs`): GDI `StretchBlt` (HALFTONE, `SRCCOPY|CAPTUREBLT`) с экранного DC в DIB шириной `Width` = 256, высота по пропорции (16…1024), JPEG WIC качество 0,6. Потоковый захват для этого не открывается.

**Сообщения.** `ScreenThumbsMessage(StreamId, On)`, `ScreenThumbMessage(StreamId, MonitorKey, Width, Height, Jpeg)`.

**Тесты.** `ScreenStreamIntegrationTests` (Explicit).

### Разрешение таймера и режим экономии

**Что делает.** Пока идёт хоть один поток, процесс держит системный таймер 1 мс: иначе ожидания округляются до ~15,6 мс, что ломает и равномерность кадров, и насос кодера.

**Как устроено.** `MediaTimerResolution.cs`: `Hold()` — счётчик пользователей, `timeBeginPeriod(1)` на первом, `timeEndPeriod(1)` на последнем; отсутствие winmm не ошибка. Режим экономии (`node.Power`): реже опрос кодера (8 мс вместо 2) и указателя (16 вместо 8), ниже предел частоты.

**Тесты.** Отдельных не найдено.

### Окно-источник, ввод зрителя и перетаскивание окна (кратко)

**Что делает.** `ScreenSource` сообщает о сворачивании окна (`Describe()` с `Minimized`), выполняет `WindowCommand.Restore`, выводит окно вперёд перед нажатием, вводит указатель/касания/клавиши/текст зрителя при `AllowControl`, а при окне администратора впереди шлёт `InputBlockedMessage` не чаще раза в 10 с. Инжекторы описаны в разделе про ввод.

**Как устроено.** `Capture/WindowDragWatcher.cs` — WinEvent-хук `EVENT_SYSTEM_MOVESIZESTART/END` в отдельном потоке: `Moving`, `MarkSent` (вернуть окно на место), `MarkParkOnRelease` + `ReleasedForPark`. `Capture/WindowPlacer.cs` — `PlaceInside(window, monitor)`: по центру, не больше монитора. Используются `ScreenStreamingService.WindowMove`.

**Тесты.** `WindowPlacerTests`, `TileAndCodecTests`, `PointerTests`.

### Настройки `ScreenSettings`, которые читает конвейер

| Свойство | По умолчанию | Где читается |
|---|---|---|
| `MaxFps` | 60 | `ScreenSource` (предел частоты, fps кодера) |
| `VideoBitrateKbps` | 12 000 | `ScreenSource.ScaleBitrate` (H.264 и HEVC) |
| `Quality` | 70 | `ScreenSource.BuildJpegMessages` |
| `SharpStill` | true | `ScreenSource.LoopAsync` |
| `AllowControl` | true | `ScreenSource` (описание потока, ввод) |
| `VideoCodec` | Auto | `ScreenStreamingService.NegotiateVideo/WithFormat` |
| `AllowViewing` | true | `ScreenStreamingService.HandleRequestAsync` |
| `DirectPresent` | true | зритель |
| `ShowStats` | false | зритель |
| `PowerSettings.Saving` / `Fps` | OnBattery / 30 | через `PowerMode` |

Остальные свойства `ScreenSettings` конвейер источника не читает.

### Расхождения документации с кодом и замеченное в коде

- README («Экран и окна»): «по умолчанию не больше 30 кадров/с» — в коде `MaxFps` = 60; 30 — только `PowerSettings.Fps`. Там же «частота кадров сама подстраивается» — `StreamPacing` меняет только битрейт, частоту ограничивает окно отправки.
- ARCHITECTURE §5.4 и ранние записи раздела 10 устарели: «курсор рисуется самим WGC» (указатель идёт отдельно; о `Cursor`/`CursorShape` и `DrawsPointer` в документе записей нет); «CPU-конвертация, GPU — следующий шаг» (есть `GpuNv12Converter`/`FeedTexture`); `ScreenCapabilityMessage` (в коде `DeviceCapabilitiesMessage`); «не больше двух неподтверждённых» (`MaxInFlight` = 3); «`Encode` ждёт до 100 мс» (`OutputWait` = 35 мс, поток использует `Feed` + `CollectLoopAsync`); «ключевой кадр при смене обрезки — кодер пересоздаётся» (пересоздаётся только при смене размера или формата). `MediaTimerResolution` и «замерший» ключевой кадр через 180 мс в ARCHITECTURE не описаны.
- XML-комментарий `ScreenCodec` устарел: у `VideoFrame` указано «u16 reserved» (на деле cropId + резерв), у `Ack` нет paintMs, у `Crop` нет id; комментарий про `VideoFrame` стоит над `StillHeader`.
- Запрос частей при приближении включён только при `Codec == "h264"`: на HEVC-потоке не работает; README этого не оговаривает.
- После перехода видеопотока на плитки `VideoLink` остаётся в `Carrying`: строка журнала и `ScreenStatsMessage.Udp` продолжают сообщать «UDP», хотя плитки идут по TCP; сторож хвоста при этом тоже остаётся активным.
- `ScreenStreamMessage.CropTags` источник объявляет всегда, включая поток плиток, где имён нет.
- Значение `VideoCodec.H264` ничем не отличается от `Auto` (в комментарии к перечислению так и сказано: «same as Auto»): в `NegotiateVideo` проверяется только `!= Jpeg`. Описание `VideoBitrateKbps` говорит про H.264, но значение действует и для HEVC.
- Чёткий снимок и «замерший» ключевой кадр связаны: снимок возможен только после того, как через 180 мс отправлен целый кадр (`_settled`), то есть на каждую паузу длиннее 0,6 с уходят и ключевой кадр, и JPEG.
