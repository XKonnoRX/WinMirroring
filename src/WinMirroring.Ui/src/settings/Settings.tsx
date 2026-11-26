import { useCallback, useEffect, useState } from 'react'
import { invoke, on } from '../bridge'
import { Choice, Num, Section, Text, Toggle } from '../ui/controls'
import { SettingsState } from './model'
import './Settings.css'

const pages = [
  { id: 'device', title: 'Устройство' },
  { id: 'input', title: 'Ввод' },
  { id: 'screen', title: 'Экран' },
  { id: 'audio', title: 'Звук' },
  { id: 'clipboard', title: 'Буфер обмена' },
  { id: 'commands', title: 'Команды' },
  { id: 'network', title: 'Сеть' },
  { id: 'startup', title: 'Запуск и обновления' },
  { id: 'health', title: 'Диагностика' },
] as const

type PageId = (typeof pages)[number]['id']

export function Settings() {
  const [state, setState] = useState<SettingsState | null>(null)
  const [page, setPage] = useState<PageId>('device')
  const [status, setStatus] = useState<string | null>(null)
  const [address, setAddress] = useState('')
  const [installing, setInstalling] = useState(false)

  const load = useCallback(async () => setState(await invoke<SettingsState>('settings.get')), [])

  useEffect(() => {
    void load()
    return on('settings.changed', () => void load())
  }, [load])

  const set = useCallback(
    async (key: string, value: string | number | boolean, on?: boolean) => {
      const answer = await invoke<string | null>('settings.set', { key, value, on })
      if (answer) setStatus(answer)
      await load()
    },
    [load],
  )

  const run = useCallback(
    async (name: string, streamId = 0) => {
      const answer = await invoke<string | null>('settings.command', { name, streamId })
      if (answer) setStatus(answer)
      await load()
    },
    [load],
  )

  if (!state) return <div className="settings-loading">Загрузка…</div>

  return (
    <div className="settings">
      <nav className="settings-nav">
        <div className="settings-brand">
          <strong>{state.device.name}</strong>
          <span>{state.device.version}</span>
        </div>
        {pages.map((item) => (
          <button
            key={item.id}
            className={`settings-tab${page === item.id ? ' is-on' : ''}`}
            onClick={() => setPage(item.id)}
          >
            {item.title}
          </button>
        ))}
      </nav>

      <main className="settings-page">
        {page === 'device' && (
          <>
            <Section title="Экономия заряда">
              <Choice
                label="Экономить заряд"
                hint="Что экономится: вдвое меньше кадров, реже маяк в сеть и реже пульс между устройствами. То, что ничего не стоит, работает всегда и в этот режим не входит."
                value={state.power.saving}
                options={state.power.options}
                onChange={(v) => void set('power.saving', v)}
              />
              <Num
                label="Кадров в секунду при экономии"
                suffix="к/с"
                value={state.power.fps}
                onChange={(v) => void set('power.fps', v)}
              />
              <p className="note">
                {state.power.savingNow
                  ? 'Сейчас экономия включена: это устройство и отдаёт, и просит меньше кадров.'
                  : state.power.onBattery
                    ? 'Устройство на батарее, но экономия сейчас выключена.'
                    : 'Устройство питается от сети — экономия не нужна.'}{' '}
                Просьба о кадрах уходит и на ту сторону: когда на батарее тот, кто смотрит, передающий снижает
                частоту сам.
              </p>
            </Section>

            <Section title="Это устройство">
              <Text
                label="Имя устройства"
                hint="Под этим именем его видят остальные. Сохраняется сразу, как только вы уйдёте из поля или нажмёте Enter; остальные узнают новое имя в течение нескольких секунд."
                value={state.device.name}
                onChange={(v) => void set('device.name', v)}
              />
              <div className="facts">
                <dt>Идентификатор</dt>
                <dd className="mono">{state.device.id}</dd>
                <dt>Отпечаток</dt>
                <dd className="mono">{state.device.fingerprint}</dd>
                <dt>Сеть</dt>
                <dd>{state.device.network}</dd>
              </div>
            </Section>
          </>
        )}

        {page === 'input' && (
          <>
            <Section title="Общий ввод">
              <Toggle
                label="Делить мышь и клавиатуру с другими устройствами"
                checked={state.input.enabled}
                onChange={(v) => void set('input.enabled', v)}
              />
              <Num
                label="Сопротивление края"
                hint="Насколько сильно нужно упереть курсор в край экрана, чтобы он перешёл на соседнее устройство."
                suffix="пикселей"
                value={state.input.edgeResistance}
                onChange={(v) => void set('input.edgeResistance', v)}
              />
              <Toggle
                label="Синхронизировать раскладку клавиатуры"
                hint="Устройство, которым вы управляете, переключается на ваш язык, а язык, выбранный там, возвращается вместе с курсором. Нужный язык должен быть добавлен на обоих."
                checked={state.input.syncKeyboardLayout}
                onChange={(v) => void set('input.syncKeyboardLayout', v)}
              />
            </Section>

            <Section title="Сглаживание">
              <Toggle
                label="Сглаживать движение мыши при нестабильной сети"
                hint="Работает на управляемом устройстве: пакеты, пришедшие пачкой, проигрываются с исходными интервалами. Саму задержку сети это не убирает."
                checked={state.input.smoothing}
                onChange={(v) => void set('input.smoothing', v)}
              />
              <Num
                label="Предел буфера"
                suffix="мс"
                disabled={!state.input.smoothing}
                value={state.input.maxSmoothingDelay}
                onChange={(v) => void set('input.maxSmoothingDelay', v)}
              />
              {state.input.smoothingText && <p className="note">{state.input.smoothingText}</p>}
            </Section>

            <Section title="Окна администратора">
              <Toggle
                label="Управлять окнами администратора через помощника"
                hint={state.input.adminControlText}
                checked={state.input.adminControl}
                onChange={(v) => void set('input.adminControl', v)}
              />
              {state.input.adminHelper && (
                <>
                  <p className="note">
                    {state.input.adminNoPrompt
                      ? 'Помощник запускается через задание планировщика: права уже выданы, запрос больше не появляется.'
                      : 'Сейчас права запрашиваются при каждом запуске. Можно выдать их один раз — приложение создаст задание планировщика, которое запускает помощника без запроса.'}
                  </p>
                  {state.input.adminNoPrompt ? (
                    <button className="btn" onClick={() => void run('adminPrompt')}>
                      Спрашивать права снова
                    </button>
                  ) : (
                    <button className="btn btn-accent" onClick={() => void run('adminNoPrompt')}>
                      Выдать права один раз
                    </button>
                  )}
                </>
              )}
              <p className="note">{state.input.elevationText}</p>
              <button className="btn" disabled={!state.input.canRestartElevated} onClick={() => void run('restartElevated')}>
                Перезапустить от имени администратора
              </button>
            </Section>
          </>
        )}

        {page === 'screen' && (
          <>
            <Section title="Доступ к этому экрану">
              <Toggle
                label="Разрешить смотреть экран и окна"
                checked={state.screen.allowViewing}
                onChange={(v) => void set('screen.allowViewing', v)}
              />
              <Toggle
                label="Разрешить управлять из окна просмотра"
                disabled={!state.screen.allowViewing}
                checked={state.screen.allowControl}
                onChange={(v) => void set('screen.allowControl', v)}
              />
              <Toggle
                label="Перетаскивать окна за заголовок через край экрана"
                disabled={!state.screen.allowViewing}
                checked={state.screen.windowDrag}
                onChange={(v) => void set('screen.windowDrag', v)}
              />
              <Toggle
                label="Прятать окно отсюда, пока оно показывается там"
                hint="Окно паркуется на виртуальном мониторе, чтобы не висеть в двух местах сразу. Нужен драйвер виртуального монитора."
                disabled={!state.screen.allowViewing}
                checked={state.screen.hideSentWindow}
                onChange={(v) => void set('screen.hideSentWindow', v)}
              />
            </Section>

            <Section title="Экран другого устройства">
              <Toggle
                label="Открывать экранную клавиатуру при касании поля ввода"
                hint="Коснулись пальцем текстового поля в трансляции — здесь открывается сенсорная клавиатура, коснулись другого места — закрывается. Выключите, если у этого устройства своя клавиатура. Нужно, чтобы и то устройство было обновлено."
                checked={state.screen.autoKeyboard}
                onChange={(v) => void set('screen.autoKeyboard', v)}
              />
            </Section>

            <Section title="Открытые трансляции">
              <Num
                label="Закрывать свёрнутый экран через"
                hint="Свёрнутая трансляция экрана продолжает стоить другому устройству захвата и передачи. Открыть её снова — одно нажатие. 0 — не закрывать. Копии окон и режим второго монитора не трогаются: копия сворачивается вслед за самим окном, а второй монитор отключают явно."
                suffix="минут"
                value={state.screen.closeUnusedMinutes}
                onChange={(v) => void set('screen.closeUnusedMinutes', v)}
              />
            </Section>

            <Section title="Качество">
              <Choice
                label="Кодек изображения"
                hint="H.264 кодирует кадр целиком с аппаратным ускорением, JPEG передаёт только изменившиеся участки. «Авто» выбирает H.264, когда его поддерживают оба устройства."
                value={state.screen.codec}
                options={state.screen.codecs}
                onChange={(v) => void set('screen.codec', v)}
              />
              <Num label="Битрейт H.264" suffix="кбит/с" value={state.screen.videoBitrate} onChange={(v) => void set('screen.videoBitrate', v)} />
              <Num label="Качество JPEG" suffix="30–95" value={state.screen.quality} onChange={(v) => void set('screen.quality', v)} />
              <Num label="Кадров в секунду, не больше" value={state.screen.maxFps} onChange={(v) => void set('screen.maxFps', v)} />
            </Section>

            <Section title="Второй монитор">
              <p className="note">{state.screen.virtualDisplay}</p>
              <div className="row">
                <button
                  className={`btn${state.screen.virtualDisplayInstalled ? '' : ' btn-accent'}`}
                  disabled={installing}
                  onClick={() => {
                    setInstalling(true)
                    void run('installVirtualDisplay').finally(() => setInstalling(false))
                  }}
                >
                  {installing
                    ? 'Устанавливаю…'
                    : state.screen.virtualDisplayInstalled
                      ? 'Обновить драйвер'
                      : 'Установить драйвер'}
                </button>
              </div>
            </Section>

            <Section title="Что сейчас показывается другим">
              {state.screen.sources.length === 0 ? (
                <p className="note">Сейчас ничего не передаётся.</p>
              ) : (
                state.screen.sources.map((source) => (
                  <div key={source.streamId} className="row is-field">
                    <div className="row-text">
                      <span className="row-label">{source.title}</span>
                      <p className="hint">→ {source.peer}</p>
                    </div>
                    <button className="btn" onClick={() => void run('stopSource', source.streamId)}>
                      Остановить
                    </button>
                  </div>
                ))
              )}
            </Section>
          </>
        )}

        {page === 'commands' && (
          <Section title="Выполнение команд">
            <Toggle
              label="Разрешить выполнение команд с других устройств"
              checked={state.shell.allowCommands}
              onChange={(v) => void set('shell.allowCommands', v)}
            />
            <p className="note">
              Спаренное устройство сможет открыть здесь консоль и запускать команды от вашего имени — так же, как
              если бы вошло по SSH. Первое разрешение каждому устройству вы подтверждаете здесь, и оно действует до
              перезапуска приложения; об открытой консоли приложение сообщает уведомлением.
            </p>
            <Toggle
              label="Разрешить консоль с правами администратора"
              disabled={!state.shell.allowCommands}
              checked={state.shell.allowElevatedCommands}
              onChange={(v) => void set('shell.allowElevatedCommands', v)}
            />
            <p className="note">
              Это больше, чем обычная консоль: с правами администратора в системе можно изменить всё что угодно.
              Поэтому у неё отдельное разрешение, которое вы подтверждаете здесь, и отдельный запрос Windows — при
              запуске помощника, который её выполняет. Оба спрашиваются один раз: помощник работает до перезапуска
              приложения, и после подтверждения это устройство открывает такие консоли без новых вопросов.
            </p>
          </Section>
        )}

        {page === 'audio' && (
          <>
            <Section title="Передача звука">
              <Toggle label="Передавать звук этого устройства на другие" checked={state.audio.send} onChange={(v) => void set('audio.send', v)} />
              <Toggle
                label="Звук следует за вводом"
                hint="Играет там, откуда управляют этим устройством."
                disabled={!state.audio.send}
                checked={state.audio.followInput}
                onChange={(v) => void set('audio.followInput', v)}
              />
              <Choice
                label="Всегда передавать на"
                disabled={!state.audio.send}
                value={state.audio.pinnedId}
                options={state.audio.targets.map((t) => ({ value: t.id, label: t.name }))}
                onChange={(v) => void set('audio.pinnedId', v)}
              />
              <Toggle
                label="Выключать здесь динамики, пока звук играет на другом устройстве"
                disabled={!state.audio.send}
                checked={state.audio.muteLocal}
                onChange={(v) => void set('audio.muteLocal', v)}
              />
            </Section>

            <Section title="Приём">
              <Toggle
                label="Воспроизводить звук, который присылают другие устройства"
                checked={state.audio.receive}
                onChange={(v) => void set('audio.receive', v)}
              />
              <Num label="Битрейт Opus" suffix="кбит/с" value={state.audio.bitrate} onChange={(v) => void set('audio.bitrate', v)} />
              <Num
                label="Буфер воспроизведения"
                hint="Меньше буфер — меньше задержка, но больше риск щелчков на нестабильном Wi-Fi. Обычно хватает 30–60 мс."
                suffix="мс"
                value={state.audio.buffer}
                onChange={(v) => void set('audio.buffer', v)}
              />
              <p className="note">{state.audio.status}</p>
            </Section>
          </>
        )}

        {page === 'clipboard' && (
          <Section title="Общий буфер обмена">
            <Toggle label="Делиться буфером обмена" checked={state.clipboard.enabled} onChange={(v) => void set('clipboard.enabled', v)} />
            <Toggle
              label="Передавать скопированные файлы и папки"
              hint="Файлы не пересылаются при копировании: на другом устройстве они появляются в буфере и скачиваются только при вставке."
              disabled={!state.clipboard.enabled}
              checked={state.clipboard.shareFiles}
              onChange={(v) => void set('clipboard.shareFiles', v)}
            />
            <Toggle
              label="Перетаскивать файлы мышью через край экрана"
              hint="Начните тянуть файлы и уведите курсор на соседнее устройство, не отпуская кнопку."
              checked={state.clipboard.dragDrop}
              onChange={(v) => void set('clipboard.dragDrop', v)}
            />
            <Num label="Текст и картинки до" suffix="МБ" value={state.clipboard.maxInline} onChange={(v) => void set('clipboard.maxInline', v)} />
            <Num
              label="Как обычные файлы для любых программ, до"
              hint="Наборы до этого размера вставляются и в программы, которые понимают только настоящие пути. Большие — только в Проводник."
              suffix="МБ"
              value={state.clipboard.directPaste}
              onChange={(v) => void set('clipboard.directPaste', v)}
            />
            {state.clipboard.activity && <p className="note">Последнее действие: {state.clipboard.activity}</p>}
          </Section>
        )}

        {page === 'network' && (
          <>
            <Section title="Интерфейсы и подсети">
              <p className="note">Узел слушает и ищет устройства только в отмеченных подсетях. Если не отмечено ничего, используются все.</p>
              {state.network.interfaces.map((nic) => (
                <Toggle
                  key={nic.id}
                  label={
                    <>
                      {nic.name} <span className="mono dim">{nic.address}</span> {nic.vpn && <span className="chip">VPN</span>}
                    </>
                  }
                  hint={nic.details}
                  checked={nic.selected}
                  onChange={(v) => void set('network.interface', nic.id, v)}
                />
              ))}
              <button className="btn" onClick={() => void run('refreshInterfaces')}>Обновить список</button>
            </Section>

            <Section title="Порты">
              <Num label="Обнаружение, UDP" value={state.network.discoveryPort} onChange={(v) => void set('network.discoveryPort', v)} />
              <Num label="Транспорт, TCP" value={state.network.transportPort} onChange={(v) => void set('network.transportPort', v)} />
              <div className="buttons">
                <button className="btn btn-accent" onClick={() => void run('applyNetwork')}>Применить</button>
                <button className="btn" onClick={() => void run('addFirewall')}>Добавить правило брандмауэра…</button>
              </div>
            </Section>

            <Section title="Адреса вручную">
              <p className="note">
                Через VPN широковещание не ходит: укажите имя или IP устройства, при необходимости с портом обнаружения (host:47620).
                Добавлять и убирать их можно и в окне устройств.
              </p>
              {state.network.addresses.map((item) => (
                <div key={item} className="row is-field">
                  <span className="mono">{item}</span>
                </div>
              ))}
              <div className="buttons">
                <input
                  className="field"
                  placeholder="имя или IP"
                  value={address}
                  onChange={(e) => setAddress(e.target.value)}
                />
                <button
                  className="btn"
                  onClick={async () => {
                    if (!address.trim()) return
                    setStatus(await invoke<string>('hub.command', { name: 'addAddress', text: address.trim() }))
                    setAddress('')
                    await load()
                  }}
                >
                  Добавить
                </button>
              </div>
            </Section>
          </>
        )}

        {page === 'startup' && (
          <>
            <Section title="Запуск">
              <Toggle
                label="Запускать при входе в Windows"
                hint="Приложение стартует свёрнутым в трей."
                checked={state.startup.withWindows}
                onChange={(v) => void set('startup.withWindows', v)}
              />
            </Section>
            <Section title="Обновления">
              <p className="note">{state.device.versionText}</p>
              <Toggle
                label="Обновляться автоматически из GitHub"
                hint="Приложение проверяет релизы и ставит новую версию, когда не занято передачей. Устройство без доступа к GitHub можно обновить с соседнего — из окна устройств."
                checked={state.startup.autoUpdate}
                onChange={(v) => void set('startup.autoUpdate', v)}
              />
              <div className="buttons">
                <button className="btn" onClick={() => void run('checkUpdates')}>Проверить обновления</button>
                <button className="btn btn-ghost" onClick={() => void run('openRepository')}>Страница проекта</button>
              </div>
            </Section>
          </>
        )}

        {page === 'health' && (
          <Section title="Что запустилось на этом устройстве">
            {state.health.map((item) => (
              <div key={item.name} className="row">
                <span className={`mark${item.ok ? ' is-ok' : ' is-bad'}`}>{item.ok ? '✔' : '✖'}</span>
                <div className="row-text">
                  <span className="row-label">{item.name}</span>
                  <p className="hint">{item.detail}</p>
                </div>
              </div>
            ))}
            <div className="buttons">
              <button className="btn" onClick={() => void run('openLogs')}>Открыть папку журналов</button>
            </div>
          </Section>
        )}
      </main>

      <footer className="settings-foot">{status ?? state.device.status}</footer>
    </div>
  )
}
