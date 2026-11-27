# Удалённые команды, wm-cli, выпуск и тесты

> Часть справочника [docs/FEATURES.md](../FEATURES.md). Составлено по коду версии 1.0.75; источник правды — код.


Источник — только код (состояние `main`, выпуск 1.0.75). Пометка «по коду, запуском не проверено» означает вывод из чтения кода без запуска. README.md и docs/ARCHITECTURE.md сверены; расхождения собраны в конце каждого раздела.

## Удалённые команды и консоль (WinMirroring.Shell)

Проект `src/WinMirroring.Shell` (ссылается только на `WinMirroring.Core`; `InternalsVisibleTo` для `WinMirroring.Shell.Tests`). Его используют три исполняемых файла: приложение `WinMirroring.exe` (обе стороны консоли и локальный канал), `wm-cli.exe` (клиент локального канала либо собственный узел) и `WinMirroring.InputHost.exe` (тело консоли администратора).

### Что такое удалённая команда и консоль

**Что делает.** Спаренное устройство («управляющее») открывает на другом («управляемом») консоль — постоянно живущий процесс PowerShell — и шлёт в неё команды по уже существующей TLS-сессии, получая вывод построчно по мере появления и код завершения. Заменяет вход по SSH/RDP для «посмотреть, поправить, запустить скрипт». Это **не терминал**: нет псевдоконсоли (ConPTY), нет передачи stdin работающей программе, нет сообщения об изменении размера окна, нет управляющих последовательностей; интерактивные и «рисующие экран» программы не работают (так и сказано в `RemoteConsoleWindow`).

**Как устроено.**
- Обе стороны живут в `ShellService` (`src/WinMirroring.Shell/ShellService.cs`): `_hosted` — консоли, открытые у нас другими (`(PeerId, Id) → IHostedShell`), `_mine` — консоли, открытые нами у других (`Id → RemoteConsole`), `_opening` — запросы, по которым ещё идёт вопрос человеку (`CancellationTokenSource`), `_arriving` — недособранные длинные команды (`CommandAssembly`).
- `ShellService.Start()` подписывается на `Sessions.SessionReady/SessionClosed`; на каждой готовой сессии регистрирует обработчик канала `ChannelId.Shell` (= 6, `src/WinMirroring.Core/Transport/ChannelId.cs`). `IsAttachedTo(peerId)` — признак, что обработчик уже стоит (он ставится чуть позже, чем сессия объявляет себя Ready).
- Чья это консоль, решается не по типу сообщения, а по принадлежности id: если id есть в `_mine` для этой же сессии — сообщение идёт в `RemoteConsole.OnMessage`, иначе трактуется как запрос к управляемой стороне.
- Одна «консоль» = один id (`uint`, на управляющей стороне случайный из `[1, int.MaxValue)`), один процесс PowerShell, одна команда одновременно (`RemoteConsole.Running`).
- В приложении сервис запускается шагом здоровья «Выполнение команд» (`src/WinMirroring.App/App.xaml.cs`): `shell.Confirm` — `MessageBox` Да/Нет со значком предупреждения, `shell.Notify` — уведомление в трее «WinMirroring: {title}», затем `shell.Start()` и `LocalShellServer.Start()`.

**Сообщения.** Управляющих (JSON, канал Control) сообщений у этой функции нет — всё идёт бинарно по каналу Shell, см. «Протокол канала Shell».

### Кто что может: настройки и подтверждения

**Что делает.** Ограничивает, какое устройство и когда может получить консоль.

**Настройки.** `ShellSettings` (`src/WinMirroring.Core/Settings/MediaSettings.cs`), в `settings.json` — `shell.allowCommands` и `shell.allowElevatedCommands` (camelCase), оба по умолчанию `false`. В UI: «Параметры» → раздел «Выполнение команд» (`src/WinMirroring.Ui/src/settings/Settings.tsx`), ключи моста `shell.allowCommands` / `shell.allowElevatedCommands` (`src/WinMirroring.App/Services/SettingsUi.cs`) → свойства `MainViewModel.AllowRemoteCommands` / `AllowElevatedRemoteCommands` (`src/WinMirroring.App/ViewModels/MainViewModel.Tools.cs`), которые пишут в `_node.Settings.Shell.AllowCommands` / `AllowElevatedCommands` и вызывают `SaveSettings()`. Переключатель «с правами администратора» в UI заблокирован, пока выключен первый.

**Как устроено.** Единственное место проверки — `ShellService.OpenHereAsync` (при **открытии** консоли, не при каждой команде), в таком порядке:
1. `!Settings.Shell.AllowCommands` → отказ «выполнение команд выключено на этом устройстве».
2. Запрос администраторской консоли и `!AllowElevatedCommands` → отказ «команды с правами администратора выключены на этом устройстве».
3. Если устройство ещё не в `_allowed` (или `_allowedElevated` для администраторской): нет делегата `Confirm` → отказ «здесь некому подтвердить запуск команд»; иначе управляющему шлётся `Asking`, человеку показывается вопрос («Выполнение команд» либо «Команды с правами администратора»). «Нет» → отказ «пользователь отказался» (отказ **не запоминается**, следующий запрос спросит снова). «Да» → `PeerId` заносится в словарь **до перезапуска приложения**. Обычное и администраторское согласие хранятся отдельно.
4. Проверка «не передумал ли просивший» (`GaveUp`) до запуска оболочки, после запуска и после помещения в `_hosted`: если управляющий прислал `Cancel`/`Close` на ещё не открытую консоль либо сессия закрылась, оболочка не запускается или сразу гасится (счётчик `GivenUpOnHere`).
5. Запуск оболочки; не вышло → отказ «не удалось запустить PowerShell» / «права администратора здесь не подтвердили».
6. Ожидание `shell.Ready` до 10 с, затем `Opened` с текущим каталогом и уведомление «Консоль открыта». При закрытии с той стороны — уведомление «Консоль закрыта».

**Ограничения и запасные пути.**
- Выключение настройки не закрывает уже открытые консоли и не очищает `_allowed` (по коду, запуском не проверено).
- Согласие привязано к `PeerId` и живёт в памяти процесса; сохранения на диск нет.
- В `wm-cli` (собственный узел) `Confirm` = значение `--accept`; с `--accept` и `shell.allowCommands: true` в его `settings.json` команды исполняются без человека.
- XML-комментарий у `ShellSettings.AllowElevatedCommands` говорит «подтверждается каждый раз» — устарел: код и тексты UI спрашивают один раз за запуск приложения (так с выпуска 1.0.53).

### Запуск команды на устройстве: RemoteShell

**Что делает.** Держит один PowerShell и исполняет в нём команды по очереди, сохраняя состояние (каталог, переменные) между командами.

**Как устроено** (`src/WinMirroring.Shell/RemoteShell.cs`, интерфейс `IHostedShell` в `IHostedShell.cs`: события `Output(ShellStreamKind, string)`, `Done(int, string)`, `Ended`, свойства `Ready`, `Directory`, методы `Run`, `Cancel`, `Dispose`).
- Хост: Windows PowerShell — `powershell.exe -NoProfile -NoLogo -NonInteractive -Command -` (не `pwsh`), без окна (`CreateNoWindow`), `UseShellExecute=false`, все три потока перенаправлены.
- Рабочий каталог при старте: профиль пользователя (`Environment.SpecialFolder.UserProfile`). Дальше каталог следует за командами (`cd` сохраняется).
- От чьего имени: обычная консоль — пользователь, под которым запущено приложение; администраторская — тот же код внутри повышенного помощника.
- Кодировка задаётся в четырёх местах: `StandardInputEncoding/OutputEncoding/ErrorEncoding` = UTF-8 без BOM, и первой командой в оболочке `[Console]::OutputEncoding = UTF8; $OutputEncoding = UTF8`.
- Команда уходит в stdin тремя строками: (1) `$global:LASTEXITCODE = 0; $global:WinMirroringOk = $null`; (2) `Invoke-Expression ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('<base64>')))`, где в base64 — текст команды плюс `"\n$global:WinMirroringOk = $?"`; (3) строка-маркер. Base64 нужен, чтобы кодовая страница консоли не портила не-ASCII и чтобы многострочный скрипт шёл одной строкой.
- Маркер конца команды: случайный на консоль `"wm" + Guid("N")`; строка `Write-Output "<marker> <code> <ok> <путь>"`, где `code = [int]$LASTEXITCODE`, `ok` — `$global:WinMirroringOk`, а если команда не дошла до него — `$?` после `Invoke-Expression`. Маркер ищется в любом месте строки stdout (программа могла не завершить строку переводом), текст до маркера отдаётся как вывод, сама строка-маркер не показывается.
- Код завершения: `code != 0 ? code : (ok ? 0 : 1)`, то есть код последней внешней программы, а при его отсутствии 1, если команда упала как PowerShell (в том числе ошибка разбора).
- Первая строка-маркер после запуска оболочки — её собственная: она завершает `Ready` и сообщает стартовый каталог, `Done` по ней не поднимается.
- Вывод: `BeginOutputReadLine/BeginErrorReadLine`, по одной строке на событие; stdout и stderr раздельно (`ShellStreamKind.Output/Error`). Каждая строка — отдельное сообщение `Output`.
- Таймаутов на выполнение команды **нет**. Ожидание готовности: 10 с в `ShellService`, 20 с в помощнике (по истечении консоль всё равно открывается, каталог может быть пустым).
- Остановка: `Cancel()` = `Dispose()`: закрыть stdin, ждать `GraceOnClose` = 2 с, затем `Kill(entireProcessTree: true)`. Прервать одну команду, не убив оболочку, нельзя.

**Ограничения и запасные пути.**
- Порядок строк между stdout и stderr не гарантирован (два независимых чтения).
- Незавершённая переводом строки часть вывода не видна, пока строка не закончится.
- Команда, завершающая саму оболочку (`exit N`), маркер не печатает: устройство шлёт `Close`, `Done` не приходит. В `RemoteCommand`/`wm-cli` это даёт код 65 без пояснения (по коду, запуском не проверено).

### Протокол канала Shell

**Как устроено** (`src/WinMirroring.Shell/Protocol/ShellCodec.cs`, `CommandAssembly.cs`). Little-endian. Заголовок 5 байт: `u8 тип`, `u32 id консоли`. Текст — UTF-8 с длиной `u16`; `ShellCodec.MaxText = 8192` байт на сообщение.

**Сообщения** (`ShellMessageType`, значение 7 не используется):

| Тип | № | Направление | Тело |
|---|---|---|---|
| `Open` | 1 | управляющий → устройство | пустой текст (в локальном канале — имя/id устройства) |
| `Opened` | 2 | устройство → управляющий | текст: стартовый каталог |
| `Refused` | 3 | устройство → управляющий | текст: причина по-русски, показывается как есть |
| `Run` | 4 | управляющий → устройство | текст: команда или её последний кусок; запускает собранное |
| `Output` | 5 | устройство → управляющий | `u8` поток (`ShellStream.Output=0`, `Error=1`), `u16` длина, текст (одна строка) |
| `Done` | 6 | устройство → управляющий | `i32` код, `u16` длина, текущий каталог |
| `Cancel` | 8 | управляющий → устройство | прервать (оболочка гасится, в ответ `Close`) |
| `Close` | 9 | в обе стороны | консоль закончена |
| `RunPart` | 10 | управляющий → устройство | начало/середина длинной команды, ничего не запускает |
| `OpenElevated` | 11 | управляющий → устройство | открыть с правами администратора (отдельный тип, чтобы старая сборка не выдала обычную консоль) |
| `Asking` | 12 | устройство → управляющий | текст: «спрашивают человека, ждём»; не окончание |

- Длинная команда: `ShellCodec.EncodeCommand` — если ≤ 8192 байт UTF-8, один `Run`; иначе куски по `MaxText / 3` = 2730 символов (суррогатная пара не разрывается), все `RunPart`, последний `Run`. Отправка строго по порядку (`RemoteConsole.SendInTurnAsync`, в помощнике — семафор `_writing`).
- Сборка: `CommandAssembly.Add` → `Ignored / More / Whole / TooLong`; предел `CommandAssembly.Longest = 1 << 20` символов. Один и тот же класс используется в трёх местах: `ShellService` (от пира), `LocalShellServer` (от инструмента), `ElevatedShellHost` (от приложения).
- `TooLong` на устройстве: `Output(Error, "Команда слишком длинная.")` + `Done(1)`.
- Реакция устройства: `Run`/`RunPart` на неизвестный id молча игнорируются; `Cancel` → оболочка гасится, ответ `Close`; `Close` → оболочка гасится, уведомление «Консоль закрыта»; `Cancel`/`Close` на ещё открывающуюся консоль → отмена ожидания. Самостоятельное завершение оболочки → `Close` управляющему. Закрытие сессии → все её консоли гасятся, у управляющего `RemoteConsole.OnClosed("соединение с устройством закрылось", lost: true)`.
- В журнал управляемой стороны пишется первая строка команды, а для многострочной — ещё и общая длина (`ShellService.Describe`), а также кто открыл/закрыл/получил отказ.

**Ограничения и запасные пути.**
- `ShellCodec.Clip` обрезает текст длиннее 8192 байт до 2730 символов, остаток не досылается: строка вывода длиннее 8 КБ приходит усечённой (вызывающие не режут строку на части). Проверка суррогатов в `Clip` смотрит `IsLowSurrogate` последнего оставляемого символа — в результате может остаться одиночный старший суррогат (по коду).
- После `TooLong` буфер очищается, но следующие куски той же команды продолжают копиться, и финальный `Run` запустит «хвост» скрипта, если консоль к тому моменту не закрыта (по коду, запуском не проверено). Через локальный канал такой скрипт отсекается раньше («команда не пришла целиком»).
- Повторный `Open` с тем же id от того же устройства перезаписал бы запись в `_hosted` без закрытия прежней оболочки (по коду).

### Управляющая сторона: ShellService.Open и RemoteConsole

**Как устроено** (`src/WinMirroring.Shell/RemoteConsole.cs`).
- `ShellService.Open(peerId, elevated)` требует готовую сессию (иначе `null`), создаёт `RemoteConsole`, шлёт `Open`/`OpenElevated` и возвращается сразу; ответ приходит событиями.
- События `RemoteConsole`: `Opened(directory)`, `Refused(reason)`, `Asking(question)`, `Lost(reason)` (устройство перестало отвечать — отдельно от отказа), `Output(ShellStream, text)`, `Done(code, directory)`, `Finished`. Свойства `Id`, `PeerName`, `Directory`, `Running`.
- `Run(command)` возвращает `false`, если консоль закончена, занята или команда пустая. `Cancel()` шлёт `Cancel`. `Close()` шлёт `Close` и поднимает `Finished`.
- Ошибка отправки (`IOException`/`InvalidOperationException`/`ObjectDisposedException`) → `OnClosed("соединение потеряно", lost: true)`.
- Все события приходят в потоке цикла приёма сессии — обработчики не должны блокировать.

### Окно удалённой консоли

**Что делает.** `RemoteConsoleWindow` (`src/WinMirroring.App/Views/RemoteConsoleWindow.xaml.cs`) — поле ввода строки, вывод, кнопки «Выполнить» и «Стоп».

**Как устроено.** Открывается из панели устройства: кнопки «Консоль» и администраторская (`src/WinMirroring.Ui/src/hub/Inspector.tsx`) → действия `console` / `consoleElevated` в `HubUi` → `MainViewModel.OpenConsoleOn(peerId, elevated)`. Строки stderr помечаются префиксом `! `, ненулевой код показывается как `[код выхода N]`, текущий каталог — в `WhereText`. История команд — стрелки вверх/вниз. Буфер вывода ограничен 400 000 символов (при превышении остаётся вторая половина). «Стоп» = `Cancel`: оболочка на устройстве завершается, консоль закрывается, нужно открывать новую. Закрытие окна = `Close()`. При отказе со словом «выключено» окно подсказывает, где включить настройку.

### Одна команда «как локальная»: RemoteCommand

**Что делает.** `RemoteCommand.RunAsync` (`src/WinMirroring.Shell/RemoteCommand.cs`) — найти устройство, открыть консоль, выполнить одну команду, вернуть её код. Используется и `wm-cli` (собственный узел), и `LocalShellServer` — поведение для устройства одинаково.

**Как устроено.**
- Ждёт готовую сессию с устройством, совпадающим по имени или id без учёта регистра; `DefaultWait` = 30 с. Затем до 50 × 20 мс ждёт `IsAttachedTo`.
- `Opened` → `console.Run(command)`; `Output` → колбэк; `Done` → код; `Asking` → колбэк `waiting`.
- Коды `ShellExit`: `CouldNotReach = 64` (не найдено/не спарено/сессия закрылась/устройство пропало посреди — «…перестал отвечать (…) — возможно, устройство уснуло; команда не выполнена.»/отмена ожидания), `Refused = 65` (устройство отказало: «{имя} отказал: {причина}.»; также консоль закончилась без `Done`).
- В `finally` всегда `console.Close()`; при отмене — ещё и `console.Cancel()`.

**Ограничения и запасные пути.** Пустая команда: `console.Run` вернёт `false`, результат не наступит до отмены/таймаута (по коду). Подписки на события ставятся после отправки `Open` — теоретическая гонка с мгновенным ответом.

### Консоль администратора (Elevated)

**Что делает.** Даёт PowerShell с правами администратора на управляемом устройстве; приложение при этом остаётся с обычными правами.

**Как устроено.**
- `ShellService.ElevatedShellAsync`: один помощник на запуск приложения (`_helper`, интерфейс `IElevatedHost`: `IsAlive`, `Open()`), запуск под `_helperGate` (семафор 1), чтобы два устройства не вызвали два запроса UAC. Перед запуском управляющему шлётся `Asking` «Windows спрашивает на устройстве про права администратора…», после — уведомление «Помощник с правами администратора запущен…». Умерший помощник (`IsAlive == false`) заменяется новым, снова с запросом UAC. При `DisposeAsync` сервиса помощник закрывается.
- `ElevatedHelper.TryStartAsync` (`src/WinMirroring.Shell/Elevated/ElevatedHelper.cs`): проверяет наличие `WinMirroring.InputHost.exe` рядом с приложением (`ElevatedShellPipe.HelperPath`); создаёт именованный канал `WinMirroring.Shell.<guid>` (`ElevatedShellPipe.Listen`: ACL — только текущий пользователь, 1 экземпляр, байтовый, асинхронный) и токен 32 случайных байта; запускает помощника `Process.Start` с `UseShellExecute=true`, `Verb="runas"`, скрытым окном и аргументами `--shell-pipe <имя> --shell-token <hex>` — здесь и появляется запрос UAC. Ждёт подключения `ElevatedShellPipe.HelperWait` = 120 с (включая время на запрос). Проверяет PID клиента и что его exe — именно `HelperPath` (`PipePeer`, `src/WinMirroring.Core/Local/PipePeer.cs`), шлёт токен кадром `FrameCodec` на канале Shell и требует тот же токен в ответ.
- `WinMirroring.InputHost.exe` (`src/WinMirroring.InputHost/Program.cs`): при наличии `--shell-pipe` запускает `ElevatedShellHost.RunAsync`, иначе — инъекцию ввода. Запланированная задача помощника ввода аргументов не передаёт, поэтому без запроса UAC в консольную половину попасть нельзя.
- `ElevatedShellHost.RunAsync` (`src/WinMirroring.Shell/Elevated/ElevatedShellHost.cs`): подключается к каналу (15 с), проверяет, что сервер — `WinMirroring.exe` из своего каталога (`ElevatedShellPipe.AppPath`), сверяет токен и возвращает его. Затем обслуживает много консолей по одному каналу теми же сообщениями `ShellCodec`: `Open`/`OpenElevated` с id → `RemoteShell.TryStart` и `Opened` после `Ready` (до 20 с); `Run`/`RunPart` → `CommandAssembly`; `Cancel`; `Close`. Id 0 (`ElevatedShellPipe.HelperItself`) — сам помощник: `Close` для 0 = завершиться. Запись — через отдельный цикл и неограниченный `Channel<byte[]>`. Коды выхода процесса: 2 — нет/плохие аргументы, 3 — не подключился, 4 — на другом конце не приложение, 5 — токен не совпал, 0 — штатно.
- На стороне приложения каждая консоль — вложенный `ElevatedHelper.ElevatedConsole : IHostedShell` с id от 1 (`Interlocked.Increment`). Обрыв канала → `Lost()`: все консоли заканчиваются.

**Ограничения и запасные пути.**
- Запрос UAC — один на запуск приложения; далее разрешённое устройство получает администраторские консоли без вопросов до перезапуска.
- Отклонённый запрос UAC, отсутствие помощника, чужой процесс на канале, неверный токен → отказ «права администратора здесь не подтвердили».
- Работает только там, где сервис хостит `WinMirroring.exe` и рядом лежит помощник: в `wm-cli` администраторская консоль недоступна (нет помощника в его каталоге; помощник к тому же проверяет имя exe сервера).
- Файл `ElevatedShellHost.cs` сохранён в испорченной кодировке (UTF-8 с BOM, кириллица и тире перекодированы дважды): литерал «Команда слишком длинная.» в администраторском пути уходит кракозябрами.
- Поведение при вводе в UAC учётных данных другого администратора (ACL канала — только исходный пользователь) — не выяснено.

### Локальный интерфейс для инструментов (Local)

**Что делает.** Позволяет программе на этой же машине (wm-cli) попросить запущенное приложение выполнить команду на устройстве от имени уже спаренного узла — без собственной идентичности и сопряжения.

**Как устроено.**
- `LocalShellPipe` (`src/WinMirroring.Shell/Local/LocalShellPipe.cs`): имя канала `WinMirroring-local-<SID пользователя>` (если SID не получить — имя пользователя); `ConnectWait` = 400 мс; `CreateServer` ставит владельца и единственное правило FullControl для текущего пользователя, при невозможности задать ACL создаёт канал с правами по умолчанию.
- `LocalShellServer` (`LocalShellServer.cs`): `AtOnce` = 8 экземпляров канала; `SaysWhatToRun` = 30 с на то, чтобы инструмент назвал устройство и команду; `ListenAgainAfter` = 5 с между попытками открыть канал (ошибка пишется в журнал один раз). Один инструмент — одна команда. После получения команды отдельная задача следит, не исчез ли инструмент (`NoticeTheToolLeavingAsync`), и отменяет выполнение, освобождая экземпляр канала.
- `LocalShellClient` (`LocalShellClient.cs`): `TryConnectAsync` → `null`, если приложения нет (таймаут/ошибка); `RunAsync(target, command, output, failed, ct, elevated, waiting)`.

**Сообщения.** Кадры `FrameCodec` (4 байта длины big-endian, 1 байт канала = Shell, тело) с сообщениями `ShellCodec`, id консоли всегда 0. Инструмент → приложение: `Open` или `OpenElevated` с текстом = имя/id устройства, затем `RunPart*` и `Run`. Приложение → инструмент: `Asking` (передаётся как есть), `Output`, при неудаче `Refused` с причиной и **всегда** завершающий `Done(код, "")`. Собственные причины сервера (все с кодом 64): «первым сообщением должно быть имя устройства.», «команда не пришла целиком.», «инструмент подключился и не сказал, что запускать.».

**Ограничения и запасные пути.** Ожидание устройства на этом пути всегда `RemoteCommand.DefaultWait` (30 с). Кто подключился, не проверяется — достаточно быть процессом того же пользователя; отдельного подтверждения на этой машине нет (подтверждает удалённое устройство). Канал закрыт без ответа → у клиента «приложение на этом устройстве закрыло канал, не ответив.» и код 64.

### Ограничения безопасности (сводно)

- Выключено по умолчанию; два независимых переключателя; проверка только при открытии консоли.
- Первое открытие каждым устройством подтверждает человек за управляемым устройством; согласие — до перезапуска приложения, отказ не запоминается. Об открытии и закрытии — уведомление.
- Консоль доступна только по готовой сессии спаренного устройства; обычная работает от имени вошедшего пользователя.
- Администраторская: отдельная настройка, отдельное согласие, запрос UAC один раз за запуск; обе стороны канала проверяют exe друг друга и токен; беззапросного запуска (через задачу планировщика) для неё нет.
- Команда ≤ 1 048 576 символов; текст одного сообщения ≤ 8192 байт.
- В журнал попадает только первая строка команды (у многострочной — с общей длиной), не весь скрипт.

### Тесты (только названия)

`tests/WinMirroring.Shell.Tests`: `RemoteShellTests` (7), `ShellCodecTests` (4), `ShellIntegrationTests` (20) — перечень и суть см. раздел «Тесты».

### Расхождения с README / ARCHITECTURE

- docs/ARCHITECTURE.md не упоминает `WinMirroring.Shell`, канал Shell и локальный канал вообще. README упоминает «консоль» только словом в списке инструментов устройства; настроек «Выполнение команд» и `--exec` в нём нет; в таблице «Состав» нет `WinMirroring.Shell`, `WinMirroring.InputHost`, `WinMirroring.Ui`.

## Утилита командной строки (wm-cli)

Проект `tools/WinMirroring.Cli` (`AssemblyName` = `wm-cli`, ссылки на Core, Input, Shell; пакет `Microsoft.Extensions.Logging.Console`). Файлы: `Program.cs` (верхнеуровневые инструкции), `CliOptions.cs`, `Exec.cs`, `ConsolePairingUi.cs`. Собирается вместе с решением в `tools/WinMirroring.Cli/bin/<Configuration>/net10.0-windows10.0.22621.0/wm-cli.exe`; в zip выпуска **не входит**. Версия сборки — общая `0.1.0`.

### Режимы и как утилита доходит до устройства

**Что делает.** Два режима: (1) безголовый узел WinMirroring для отладки протокола и тестов; (2) `--exec` — выполнить одну команду на устройстве и вести себя как эта команда.

**Как устроено.**
- `--exec`, путь через приложение (основной): до создания узла `LocalShellClient.TryConnectAsync` (400 мс) подключается к каналу `WinMirroring-local-<SID>`; если приложение запущено — `Exec.RunThroughAppAsync`, команду исполняет приложение своей идентичностью, узел и каталог данных не создаются.
- `--exec`, собственный узел (приложения нет): создаётся `MirroringNode` в каталоге данных утилиты, поднимается `ShellService`, вызывается `RemoteCommand.RunAsync` (`Exec.RunAsync`). Такой узел должен быть заранее спарен с устройством (`wm-cli --pair <имя>`).
- Режим узла: создаёт `MirroringNode` (`NodeOptions`: `DataDirectory`, `IncludeLoopback`, `DeviceName`, `DiscoveryPort`, `TransportPort`, `ManualAddresses`), `ConsolePairingUi`, при `--input` — `InputSharingService`, всегда — `ShellService` (`Confirm` = `--accept`, `Notify` → строка `SHELL …`). Работает до Ctrl+C, `--timeout` или `--exit-when-ready`.
- Каталог данных: `--data-dir` или `%LocalAppData%\WinMirroring\cli`. Перед созданием узла `SettingsStore.IsInUseByAnotherNode` (именованный мьютекс `Local\WinMirroring-data-<хэш пути>`): занят → сообщение и код 66.
- Ctrl+C и `--timeout` отменяют один общий `CancellationTokenSource`.

### Параметры

| Параметр | Значение |
|---|---|
| `--name <name>` | имя устройства для пиров |
| `--data-dir <dir>` | каталог настроек/идентичности (по умолчанию `%LocalAppData%\WinMirroring\cli`; чужой занятый каталог — код 66) |
| `--discovery-port <port>` | UDP-порт обнаружения (по умолчанию 47620) |
| `--port <port>` | TCP-порт транспорта (по умолчанию 47621) |
| `--manual <host[:port]>` | адрес пира для зондирования; можно повторять; порт — порт обнаружения пира |
| `--loopback` | включить loopback-интерфейс (несколько узлов на одной машине) |
| `--pair <name\|id>` | начать сопряжение с этим пиром, как только он обнаружен (одна попытка одновременно; после неудачи — повтор при следующем изменении пира) |
| `--accept` | автоматически принимать входящие запросы сопряжения; он же — ответ «да» на запрос консоли у этого узла |
| `--exit-when-ready` | выйти с кодом 0 после первой готовой сессии |
| `--timeout <seconds>` | общий предел времени работы процесса |
| `--verbose` | уровень журнала Debug (иначе Information; в `--exec` — Warning) |
| `--input` | участвовать в общем вводе (хуки и инъекция) |
| `--exec <name\|id>` | устройство, на котором выполнить команду; требует `--command` или `--command-file` |
| `-c`, `--command "<text>"` | текст команды PowerShell |
| `--command-file <path>` | прочитать команду из файла (`File.ReadAllTextAsync`); имеет приоритет над `--command` |
| `--elevated` | просить консоль с правами администратора |
| `-h`, `--help` | справка |

Без `--accept` входящее сопряжение спрашивается в консоли: `Accept? [y/N]`.

### Коды выхода

| Код | Когда |
|---|---|
| код команды | `--exec`: команда выполнена (0 — успех; 1 — упала как PowerShell без кода программы) |
| 0 | режим узла: штатный выход (Ctrl+C, `--timeout` без `--exit-when-ready`, готовая сессия при `--exit-when-ready`) |
| 1 | неизвестный аргумент, нет значения, не число, `--exec` без команды, а также `-h`/`--help` (печатается справка) |
| 2 | `--exit-when-ready` и отмена/таймаут до первой готовой сессии (`EXIT timeout`) |
| 64 | `Exec.CouldNotReach`: устройство не найдено/не спарено/уснуло, сессия закрылась, вышел `--timeout` или Ctrl+C во время ожидания; также не прочитан `--command-file` и ошибки локального канала |
| 65 | `Exec.Refused`: устройство отказало (настройка выключена, человек сказал «нет», UAC не подтверждён) либо консоль закрылась без кода |
| 66 | `Exec.DirectoryInUse`: каталог данных занят другим узлом |

### Вывод и примеры

**Как устроено.** В `--exec` до создания журнала выполняется `Console.SetOut(Console.Error)`: в stdout попадает только stdout удалённой команды (построчно), stderr команды и всё своё — в stderr. Свои сообщения имеют префикс `wm-cli: `; ожидание человека печатается как `wm-cli: <вопрос>. Ждём, команда пойдёт сразу после ответа.` В режиме узла stdout — построчные события: `DEVICE`, `INTERFACES`, `LISTENING`, `PEER`, `READY`, `CLOSED`, `PAIR REQUEST`, `PAIRING …`, `PAIRED` / `PAIR FAILED` / `PAIR ERROR`, `INPUT`, `LAYOUT`, `SHELL`, `EXIT` / `EXIT ready` / `EXIT timeout`.

Примеры (из справки и README; строка с `--elevated` составлена по описанию параметров):

```powershell
wm-cli --exec tablet -c "Get-Service W32Time | Select-Object -Expand Status"
wm-cli --exec tablet --command-file .\collect-logs.ps1 > logs.txt
wm-cli --exec tablet --elevated -c "Restart-Service Spooler"
wm-cli --pair tablet                      # один раз, только на машине без приложения
wm-cli --name B --data-dir $env:TEMP\wm-b --loopback --discovery-port 48620 --port 48621 --manual 127.0.0.1:48630 --accept
wm-cli --name A --data-dir $env:TEMP\wm-a --loopback --discovery-port 48630 --port 48631 --manual 127.0.0.1:48620 --pair B --exit-when-ready
```

### Ограничения и запасные пути

- `--timeout` в `--exec` ограничивает всё выполнение, а не только поиск устройства; по истечении — «время ожидания команды вышло.» и 64. На пути через приложение ожидание устройства всегда 30 с, на собственном узле равно `--timeout` (или 30 с).
- В справке `--timeout` описан как «код 2, если ничего не готово» — это верно только вместе с `--exit-when-ready`.
- Справка печатается в stdout и при ошибке разбора аргументов `--exec`.
- Параметра для включения `shell.allowCommands` у узла wm-cli нет — только правкой `settings.json` в его каталоге (по коду).
- Расхождение: README описывает wm-cli только как «безголовый узел для отладки протокола и автотестов»; режим `--exec` и коды 64/65/66 задокументированы лишь в справке утилиты.

## Сборка, выпуск и обновление

### Общие свойства сборки

**Как устроено.**
- `Directory.Build.props` (для всех проектов): `TargetFramework` = `net10.0-windows10.0.22621.0`, `SupportedOSPlatformVersion` = `10.0.19041.0`, `Nullable` и `ImplicitUsings` включены, `LangVersion` = `latest`, `TreatWarningsAsErrors` = `true`, `SatelliteResourceLanguages` = `en`, `AccelerateBuildsInVisualStudio` = `true`, `Version` = `0.1.0`, `Product` = `WinMirroring`, `Copyright` = «Copyright © 2026».
- `global.json` содержит только `"test": { "runner": "Microsoft.Testing.Platform" }`; версия SDK не закреплена.
- `WinMirroring.slnx`: `/src/` — App, Audio, Clipboard, Core, Input, InputHost, Screen, Shell; `/tests/` — шесть проектов `*.Tests`; `/tools/` — `WinMirroring.Cli`. `src/WinMirroring.Ui` (npm) в решение не входит.
- `.gitignore` исключает `bin/`, `obj/`, `artifacts/`, `publish/`, `TestResults/`, `node_modules/`, `src/WinMirroring.Ui/dist/`.

### Версии

**Как устроено.** Версия выпуска задаётся вручную в `src/WinMirroring.App/WinMirroring.App.csproj`: `<Version>1.0.75</Version>` (комментарий требует поднимать её с каждым выпуском). Остальные сборки (включая `WinMirroring.InputHost.exe` и `wm-cli.exe`) остаются `0.1.0`. `GithubUpdater.CurrentVersion` берёт версию входной сборки и нормализует до трёх частей; с ней сравнивается тег выпуска. Версия в `src/WinMirroring.Ui/package.json` (`1.0.0`) ни на что не влияет.

### Сборка React-интерфейса в приложение

**Как устроено** (`WinMirroring.App.csproj`, `src/WinMirroring.Ui/package.json`, `vite.config.ts`).
- UI: React 18.3.1, Vite 5.4.11, TypeScript 5.6.3, `@vitejs/plugin-react` 4.3.4. Скрипты: `dev` = `vite`, `build` = `tsc --noEmit && vite build`, `preview`.
- Vite: `base: './'` (относительные пути, файлы отдаются из папки рядом с exe), `outDir: 'dist'`, `emptyOutDir`, `target: 'chrome120'`, `chunkSizeWarningLimit: 1500`; dev-сервер `127.0.0.1:5183`, `strictPort` (запись `ui` в `.claude/launch.json`). Чтения dev-сервера приложением в коде не найдено.
- Цель `BuildUi` (`BeforeTargets="BeforeBuild"`, условие `SkipUiBuild != true`, инкрементальная: входы — `src\**`, `index.html`, `package.json`, `vite.config.ts`, `tsconfig.json`; выход — `dist\index.html`): в PATH добавляется `%ProgramFiles%\nodejs`; если нет `node_modules` — `npm.cmd ci --no-audit --no-fund`; затем `npm.cmd run build`. Оба шага с `ContinueOnError`; при ненулевом коде — предупреждение «Интерфейс на React не собран…».
- `CopyUi` (после `Build`) копирует `dist\**` в `$(OutDir)wwwroot\`; `PublishUi` (после `Publish`) — в `$(PublishDir)wwwroot\`. Обе — только если есть `dist\index.html`.
- В рантайме `WebWindow.RootDirectory` = `<папка exe>\wwwroot`, `IsAvailable` = наличие `index.html`; папка отображается на виртуальное имя хоста WebView2 (`SetVirtualHostNameToFolderMapping`). Без неё шаг здоровья «Главное окно» падает с «нет собранного интерфейса в …».

**Настройки.** MSBuild-свойство `SkipUiBuild=true` пропускает сборку UI.

**Ограничения и запасные пути.** Сочетание «предупреждение при неудаче npm» и `TreatWarningsAsErrors=true` — превращается ли оно в ошибку сборки, не выяснено (не собирал).

### Публикация и состав папки сборки

**Как устроено.**
- Команда: `dotnet publish src/WinMirroring.App/WinMirroring.App.csproj -c Release -r win-x64 --self-contained -o <папка>`.
- Цель `PublishInputHost` (после `Publish`) запускает вложенный `dotnet publish` проекта `WinMirroring.InputHost` в ту же папку с тем же RID и `SelfContained` (по умолчанию `win-x64` и `true`).
- `scripts\update.ps1` и `scripts\update.cmd` включены в проект как `None` с `CopyToOutputDirectory` и `CopyToPublishDirectory` = `PreserveNewest` — лежат в каждой сборке.
- Итог: `WinMirroring.exe` (WinExe, WPF, значок `Assets\app.ico`, `app.manifest`), `WinMirroring.InputHost.exe`, `wwwroot\`, `update.cmd`, `update.ps1`, среда выполнения .NET. `wm-cli` не публикуется.
- Локальные папки по соглашению скрипта обновления: `artifacts\publish\win-x64-staging` (новая сборка) и `artifacts\publish\win-x64` (установка).

### Выпуск (GitHub Actions)

**Что делает.** Выпуск = отправленный в репозиторий тег `v*` (по факту `vX.Y.Z`, последний `v1.0.75`, всего 76 тегов); CI собирает self-contained сборку win-x64 и публикует её как GitHub Release с одним файлом.

**Как устроено** (`.github/workflows/release.yml`, workflow «Release», `on: push: tags: 'v*'`, `permissions: contents: write`, `runs-on: windows-latest`):
1. `actions/checkout@v4`.
2. `actions/setup-dotnet@v4`, `dotnet-version: '10.0.x'`.
3. `actions/setup-node@v4`, `node-version: '22.x'`, кэш npm по `src/WinMirroring.Ui/package-lock.json`.
4. `dotnet publish src/WinMirroring.App/WinMirroring.App.csproj -c Release -r win-x64 --self-contained -o publish` (внутри собираются UI и InputHost).
5. `Compress-Archive -Path publish\* -DestinationPath WinMirroring-win-x64.zip -Force` (файлы в корне архива).
6. `softprops/action-gh-release@v2`: `tag_name` и `name` = имя тега, `files: WinMirroring-win-x64.zip`, `generate_release_notes: true`.

Артефакт: **`WinMirroring-win-x64.zip`** — это же имя жёстко зашито в `GithubUpdater.AssetName`. Принятый в репозитории порядок (по истории коммитов): поднять `<Version>` в `WinMirroring.App.csproj`, коммит «Release X.Y.Z: …» в `main`, тег `vX.Y.Z`, push.

**Ограничения и запасные пути.** CI не запускает тесты и не проверяет, что тег совпадает с `<Version>`: если версию не поднять, приложение из нового выпуска будет считать себя старым и обновляться повторно (по коду). Подписи кода и установщика нет.

### Автообновление из GitHub

**Как устроено** (`src/WinMirroring.App/Services/GithubUpdater.cs`, `App.xaml.cs`; подробности UI — в описании приложения).
- Репозиторий `XKonnoRX/WinMirroring`. Проверка: `GET https://api.github.com/repos/XKonnoRX/WinMirroring/releases/latest` (до 3 попыток, 30 с каждая), запасной путь — редирект страницы `…/releases/latest` на `…/releases/tag/vX.Y.Z` (2 попытки), адрес файла тогда `…/releases/download/<tag>/WinMirroring-win-x64.zip`. Тег разбирается после срезания `v`/`V`; обновление есть, если версия тега больше текущей.
- Расписание: при `Settings.AutoUpdate` (по умолчанию `true`) — через 8 с после старта, далее каждые 6 ч; после неудачи — через 30 мин.
- Скачивание в `%TEMP%\WinMirroring-<tag>.zip` (до 4 попыток). Автоматическая установка только когда устройство свободно (нет активности экрана и им не управляют), иначе откладывается.
- Установка: `GithubUpdater.Apply` запускает `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "<папка приложения>\update.ps1" -Source "<zip>"` без окна. Тот же `Apply` использует `PeerFilesService` для обновления сборкой, присланной соседним устройством.

### Скрипты обновления

**Что делает.** `scripts/update.cmd` — обёртка: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" %*`; при ошибке `pause`, иначе пауза 3 с. `scripts/update.ps1` закрывает приложение, зеркалирует новую сборку в папку установки и запускает приложение заново.

**Настройки** (параметры `update.ps1`): `-Source <папка|zip>`, `-Target <папка>`, `-Build`, `-NoStart`, `-DryRun`.

**Как устроено** (по шагам):
1. Константы: `WinMirroring.exe`, имя процесса `WinMirroring`, событие выхода `Local\WinMirroring.App.Exit`. `$InRepo` — есть ли `WinMirroring.slnx` уровнем выше.
2. Через `Win32_Process` ищет запущенный `WinMirroring.exe`, исключая командные строки с `--vdd-add-mode` и `--exit`; запоминает путь exe и аргументы (без `--restarted`). Пустой путь → предупреждение «запущен с правами администратора».
3. `Source` по умолчанию: папка скрипта, если в ней есть `WinMirroring.exe`; иначе в репозитории — `artifacts\publish\win-x64-staging`; иначе ошибка.
4. `Target` по умолчанию: папка запущенного приложения; иначе в репозитории — `artifacts\publish\win-x64`; иначе ошибка.
5. `-Build` (только из репозитория): `dotnet publish …WinMirroring.App.csproj -c Release -r win-x64 --self-contained true -o $Source`.
6. `Source` — `.zip`: распаковка в `%TEMP%\WinMirroring-update-<guid>`, источником становится папка, где найден `WinMirroring.exe`.
7. Проверки: в источнике есть exe; источник и цель — разные папки; цель либо пуста, либо уже содержит `WinMirroring.exe` (иначе отказ — `/MIR` удалил бы чужие файлы).
8. Кого закрывать: без `-NoStart` — все процессы `WinMirroring`; с `-NoStart` — только запущенные из папки цели (и те, чей путь не читается).
9. `-DryRun`: печатает источник, цель, список PID и строку запуска, выход 0.
10. Закрытие: открыть и установить событие `Local\WinMirroring.App.Exit` (приложение слушает его в `App.ListenForExitSignal` и выходит штатно; то же делает `WinMirroring.exe --exit`), ждать 20 с. Если события нет (старая версия) или процесс остался — `Stop-Process -Force`, ждать ещё 10 с; не удалось — ошибка с советом запустить от администратора.
11. Копирование: `robocopy <Source> <Target> /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP`, до 3 попыток с паузой 2 с; успех — код < 8. Временная распаковка удаляется. Код ≥ 8 — ошибка «файлы могли остаться частично обновлены».
12. Печатает дату `WinMirroring.dll` в цели.
13. Без `-NoStart`: `Start-Process <Target>\WinMirroring.exe` с прежними аргументами плюс `--restarted` (приложение с этим флагом ждёт мьютекс единственного экземпляра до 20 с).
14. Любая ошибка — `exit 1`.

**Ограничения и запасные пути.** Запуски с `--vdd-install` из фильтра шага 2 не исключены (по коду). Процесс `WinMirroring.InputHost` скрипт сам не закрывает — рассчитывает на его завершение вместе с приложением. Приложение, запущенное от администратора, обновляется только скриптом, запущенным от администратора.

### Расхождения с README / ARCHITECTURE

- README описывает только ручное обновление скриптом; workflow выпуска, тег `vX.Y.Z`, zip-артефакт и автообновление из GitHub в нём не описаны (поведение скрипта описано верно).
- ARCHITECTURE: «без npm приложение собирается и работает, просто открывается старое окно»; сообщение в csproj говорит «окна на WebView2 будут недоступны», а шаг здоровья «Главное окно» при отсутствии `wwwroot` завершается ошибкой. Что именно откроется — не проверял.

## Тесты

### Фреймворк и раннер

**Как устроено.** Шесть проектов в `tests/`, у всех одинаковая схема (подтверждено в каждом csproj): `OutputType` = `Exe`, `IsTestProject` = `true`, `UseMicrosoftTestingPlatformRunner` = `true`, `TestingPlatformDotnetTestSupport` = `true`; пакеты `xunit.v3` 4.0.2 и `xunit.runner.visualstudio` 4.0.1; глобальный `using Xunit`. Раннер для `dotnet test` задан в `global.json` (`Microsoft.Testing.Platform`). `WinMirroring.Screen.Tests` дополнительно включает `UseWPF` (для `DirectPresentTests`), `WinMirroring.Shell.Tests` — `Microsoft.Extensions.Logging.Abstractions`. CI тесты не запускает.

Всего примерно 388 методов с `[Fact]`/`[Theory]` (Audio 23, Clipboard 34, Core 61, Input 69, Screen 170, Shell 31), из них 26 помечены `Explicit = true`.

### Как запускать

- Из README: `dotnet test tests/WinMirroring.Core.Tests/WinMirroring.Core.Tests.csproj`; «исполняемый файл тестов можно запускать и напрямую из `bin/`» (например `tests\WinMirroring.Shell.Tests\bin\Debug\net10.0-windows10.0.22621.0\WinMirroring.Shell.Tests.exe`). Команды для всех тестов сразу в репозитории нет; для режима Microsoft.Testing.Platform у `dotnet test` в .NET 10 штатные формы — `dotnet test --solution WinMirroring.slnx` и `dotnet test --project <csproj>` (запуском не проверено; принимает ли SDK позиционный путь из README — не выяснено).
- Явные тесты: `[Fact(Explicit = true)]` по умолчанию не выполняются. Единственное указание в репозитории — комментарий в `AudioCaptureSmokeTests`: «runs only on request (--explicit only)». То есть раннеру xunit v3 передаётся `--explicit only`, обычно вместе с фильтром (`--filter-class`, `--filter-method` — стандартные ключи xunit v3, в репозитории не зафиксированы). Например: `WinMirroring.Screen.Tests.exe --explicit only --filter-class "*ScreenStreamIntegrationTests"`.
- Самопропуск: неявные тесты, которым нужно железо, сами вызывают `Assert.SkipUnless` / `Assert.SkipWhen` / `Assert.Skip` (нет аппаратного H.264/HEVC, нет Direct3D, один монитор, нет аудиовыхода, нет окна на переднем плане, нет сетевого интерфейса с подсетью).
- Коллекции без параллелизма: `HardwareVideoCollection` (тесты видеожелеза по одному) и `ProcessMemoryCollection` (`MemoryTests`).
- Интеграционные тесты поднимают настоящие узлы `MirroringNode` на loopback с временными каталогами в `%TEMP%` и сертификатами в хранилище пользователя (удаляются в `DisposeAsync`).
- Пробы (`*Probe`) — измерения, а не проверки; результаты пишут в `%TEMP%`.

### WinMirroring.Shell.Tests

| Класс | Что проверяет | Explicit / железо |
|---|---|---|
| `RemoteShellTests` | Сама оболочка: вывод и свой код выхода, раздельные потоки, каталог и переменные сохраняются между командами, не-ASCII в обе стороны, вывод приходит во время выполнения | нет; нужен настоящий `powershell.exe` |
| `ShellCodecTests` | Короткая команда — одно сообщение; длинная собирается целиком и запускается последним куском; кусок не рвёт символ; `Asking` несёт вопрос | нет |
| `ShellIntegrationTests` | Два узла на loopback: команда и код; отказ при выключенной настройке и при «нет»; каталог между командами; вывод без перевода строки; ошибка без программы; код не «прилипает»; инструмент через локальный канал (успех, причина отказа, `Asking`, молчащий инструмент, возврат экземпляров канала после убитых инструментов); длинный скрипт; администраторская консоль (запрет, повторный вопрос после отказа, одно согласие на запуск — с подставным `IElevatedHost`); брошенная до открытия консоль не открывается; закрытие доходит до устройства; «перестал отвечать» ≠ «отказал» | нет; настоящий PowerShell и loopback, UAC не трогает |

### WinMirroring.Core.Tests

| Класс | Что проверяет | Explicit / железо |
|---|---|---|
| `ButtonBarSettingsTests` | Стандартные наборы кнопок у нового устройства, сохранение, миграция старых настроек, независимость клона | нет |
| `ButtonPresetProgramTests` | Выбор набора кнопок по имени активной программы | нет |
| `CodecTests` | Маяк, кадры `FrameCodec` (в том числе слишком большие и по частям), управляющие сообщения, списки мониторов, совместимость со старыми пирами | нет |
| `DataDirectoryTests` | Занятость каталога данных другим узлом (`IsInUseByAnotherNode`), общий каталог всё же читается и пишется | нет |
| `HeartbeatTests` | Пульс: запись вовремя, незаписанный пульс завершает сессию | нет |
| `IPv4SubnetTests` | Сеть, широковещательный адрес, принадлежность, префикс из маски | нет |
| `InterfacePinTests` | Привязка соединения к интерфейсу пира | нет; один тест пропускается без интерфейса с подсетью |
| `NodeIntegrationTests` | Два узла на loopback: обнаружение, сопряжение и отказ, переподключение, разрыв сопряжения с той стороны, переименование, знакомство через общее доверенное устройство, смена сети | нет; loopback, хранилище сертификатов |
| `PairingCryptoTests` | Одинаковый шестизначный код на обоих устройствах, разбор ручного адреса | нет |
| `PowerModeTests` | Режим экономии: когда включается, частота кадров только понижается, интервалы | нет |
| `TailscalePeerTests` | Разбор списка машин Tailscale и выбор только онлайн-машины с именем спаренного устройства | нет (без Tailscale) |

### WinMirroring.Audio.Tests

| Класс | Что проверяет | Explicit / железо |
|---|---|---|
| `AudioCaptureSmokeTests` | Захват стартует и отдаёт целые кадры | **Explicit**; настоящее аудиоустройство |
| `AudioPipelineTests` | Кодирование Opus → `IncomingAudioStream`: синус доходит, потери маскируются и считаются, восстановление из запасной копии, чужие/поддельные/опоздавшие пакеты, заголовок шифра | нет |
| `AudioRoutingTests` | Правила маршрутизации звука: приоритеты, отказ, отключённые устройства, разрешение встречных потоков | нет |
| `EndpointVolumeTests` | Освобождение устройства снимает колбэк громкости; замер времени ответа выходов | 1 из 2 **Explicit**; неявный пропускается без активного аудиовыхода |
| `JitterBufferTests` | Буфер воспроизведения: тишина до цели, затухание, подрезание задержки, адаптация цели | нет |

### WinMirroring.Clipboard.Tests

| Класс | Что проверяет | Explicit / железо |
|---|---|---|
| `BundleTransferTests` | Много файлов одним потоком между двумя узлами: точность, сжатие текста, чтение Explorer по одному файлу, совместимость со старым способом | нет; loopback |
| `ClipboardPrivacyTests` | Метки менеджеров паролей: что не отправляется | нет |
| `ClipboardSyncLiveTests` | Текст от пира попадает в буфер обмена | **Explicit**; настоящий буфер обмена и сессии |
| `ClipboardThreadLiveTests` | Запись видна другому слушателю без эха; помеченное содержимое не читается и не шлётся | **Explicit** (оба); настоящий буфер обмена |
| `CodecTests` | Кодек буфера: сжатие, предел, усечённые данные, предложения, сообщения перетаскивания и файлов, небезопасные пути | нет |
| `FileTransferIntegrationTests` | Настоящий файл по спаренной сессии | нет; loopback |
| `FilesTests` | Развёртывание папок в предложении, дубли имён, удалённый поток с перемоткой, `DataObject`, воссоздание дерева | нет; временные файлы |
| `SendFilesIntegrationTests` | Отправка файлов выбранному устройству: приём, отказ с причиной, устройство без поддержки, неподключённое устройство, закреплённое предложение | нет; loopback |

### WinMirroring.Input.Tests

| Класс | Что проверяет | Explicit / железо |
|---|---|---|
| `ElevatedHelperTaskTests` | XML задачи планировщика для помощника ввода; Windows понимает определение; имя канала | нет; вызывает `schtasks.exe` (с правами администратора задача создаётся и сразу удаляется) |
| `FreeLayoutTests` | Свободная раскладка: переход только по общему краю и на той же высоте, сетка для старых устройств | нет |
| `InjectionCodecTests` | Кодек инъекции для помощника: вызовы, рукопожатие с токеном, кадрирование, маршрутизатор, отпускание зажатого | нет |
| `InputCodecTests` | Кодек событий ввода, обрезанные и неизвестные данные | нет |
| `KeyChordTests` | Разбор и запись сочетаний клавиш и последовательностей | нет |
| `KeyboardLayoutTests` | Подбор раскладки по языку, отслеживание переключений | нет; читает установленные раскладки, один тест пропускается без окна на переднем плане |
| `LayoutAndEdgeTests` | Соседи по сетке, края, пересчёт координат | нет |
| `LayoutRelayIntegrationTests` | Три узла цепочкой A—B—C: раскладка доходит до несоединённого устройства | нет; loopback |
| `LayoutVersionTests` | Версии раскладки и разрешение равенства | нет |
| `MonitorLayoutTests` | Ячейка на монитор, переходы с конкретного монитора, виртуальный монитор | 1 из 11 **Explicit** (`TheCatalogSeesThisMachinesMonitors` — настоящие мониторы) |
| `PlayoutSchedulerTests` | Сглаживающий буфер ввода: задержка, всплески, переполнение счётчика времени | нет |

### WinMirroring.Screen.Tests

| Класс | Что проверяет | Explicit / железо |
|---|---|---|
| `BgraScalerTests` | Уменьшение кадра в памяти: цвет, обрезка, градиент, усреднение | 1 из 6 **Explicit** (замер времени) |
| `ConversionCostProbe` | Замер стоимости преобразования цвета | **Explicit**; измерение |
| `DirectPresentTests` | Картинка, оставленная на видеокарте, — то, что WPF показывает на экране | **Explicit**; настоящее окно, включённый рабочий стол, GPU |
| `DisplayColourProbe` | Цветовое пространство каждого монитора | **Explicit**; настоящие мониторы |
| `EncoderPipelineTests` | Подача кадров в кодер отдельно от получения; отказ от кадра не того размера | 1 из 4 **Explicit**; аппаратный H.264 (иначе пропуск) |
| `FlowControlTests` | Окно неподтверждённых кадров и регулятор битрейта | нет |
| `FrameSizeProbe` | Размеры ключевых и обычных кадров | **Explicit**; аппаратный H.264 |
| `GpuDecodingTests` | Декодирование на карте и на процессоре дают одну картинку | 2 из 5 **Explicit**; GPU (иначе пропуск) |
| `GpuEncodingTests` | Преобразование и обрезка на карте, кодирование прямо из текстуры | 1 из 4 **Explicit**; Direct3D и аппаратный кодер (иначе пропуск) |
| `HevcTests` | Распознавание ключевых кадров и формата, круг через HEVC | нет; аппаратный HEVC (иначе пропуск) |
| `LatencyTests` | Выбор пути UDP/TCP, сопоставление подтверждений кадрам | нет |
| `CatchUpDecodingTests` (в `LatencyTests.cs`) | Кадр, декодированный без чтения, не ломает следующий | нет; аппаратный H.264 (иначе пропуск) |
| `MemoryTests` | Просмотр экрана и декодирование плиток не наращивают память процесса | нет; декодер H.264 (иначе пропуск), своя коллекция |
| `MfOutputSampleTests` | Владение буферами выходного образца Media Foundation | нет |
| `MonitorChoiceTests` | Монитор выбирается по имени, индекс — запасной путь | нет; настоящие мониторы (иначе пропуск) |
| `MonitorNeighbourTests` | Какой монитор «рядом» для свайпа, пороги жеста | нет |
| `Nv12ConverterTests` | Быстрое преобразование NV12↔BGRA совпадает с прямым побайтно | нет |
| `OutputSizeTests` | Размер картинки под окно зрителя | нет |
| `PointerTests` | Сообщения позиции и формы указателя, отрисовка стрелки, координаты | нет; системный курсор (`LoadCursorW`) |
| `RemoteInputTrackerTests` | Распознавание нажатия зрителя в его точке | нет |
| `ResendTests` | Досылка потерянных кусков кадра, подтверждения по UDP | нет |
| `ScreenStreamIntegrationTests` | Настоящий захват основного монитора между двумя узлами: JPEG, H.264, HEVC, размер под зрителя, часть экрана, повторное открытие, картинки на карте, смена монитора, потери UDP, показатели и отметка, чёткий кадр | 11 из 12 **Explicit**; дисплей, GPU. Неявный `ExtendWithoutTheDriverIsRefusedWithAReason` пропускается, если драйвер виртуального монитора установлен |
| `SharedSurfaceTests` | Общая поверхность Direct3D 11/9 — одна и та же картинка | нет; GPU и Direct3D 9 (иначе пропуск) |
| `StillCodecTests` | Чёткий неподвижный кадр: полоски в один пиксель, сообщение | нет |
| `TileAndCodecTests` | Плитки JPEG: первая отправка и изменения, обрезка, сообщения ввода/касаний/текста | нет |
| `TouchInjectorTests` | Поддерживающие посылки касаний идут только пока что-то нажато | нет (подставной приёмник) |
| `VideoCodecTests` | Сообщение видеокадра, NV12, цвета H.264 на 1080p, смена размера потока, проба возможностей | нет; часть требует аппаратный H.264 (иначе пропуск) |
| `VideoDatagramTests` | Нарезка кадра на датаграммы, восстановление по чётности, порядок выдачи | нет |
| `ViewEndingTests` | Какие просмотры переоткрываются сами | нет |
| `ViewPartTests` | Запрос видимой части экрана при увеличении | нет |
| `VirtualDisplayTests` | Выбор режима виртуального монитора, его положение, запись разрешения в настройки драйвера | нет; временные файлы |
| `WindowPlacerTests` | Размещение окна на мониторе | нет |

Вспомогательные файлы без тестов: `FileLogger.cs` (журнал в файл для живых тестов), `HardwareVideoCollection.cs` (определение коллекции).

### Расхождения с README / ARCHITECTURE

- README в «Составе» перечисляет только `tests/WinMirroring.Core.Tests`; фактически проектов шесть. ARCHITECTURE (структура каталогов и «40 тестов») отражает ранний этап — сейчас около 388 тестовых методов.
- Способ запуска явных тестов и всех тестов разом в документации не описан.
