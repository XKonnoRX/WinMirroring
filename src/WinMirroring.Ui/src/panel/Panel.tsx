import { useCallback, useEffect, useRef, useState } from 'react'
import { invoke, on } from '../bridge'
import { PanelState } from './model'
import './Panel.css'

export function Panel() {
  const [state, setState] = useState<PanelState | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => setState(await invoke<PanelState>('panel.get')), [])

  useEffect(() => {
    void load()
    const stop = on('panel.changed', () => void load())
    // The panel is always on top of everything, so it has to notice changes by itself as well.
    const timer = window.setInterval(() => void load(), 900)
    return () => {
      stop()
      window.clearInterval(timer)
    }
  }, [load])

  // Where the last press landed on the screen: the app checks it against what a watching device last pressed.
  const at = useRef({ screenX: 0, screenY: 0 })
  useEffect(() => {
    const note = (event: PointerEvent) => (at.current = { screenX: event.screenX, screenY: event.screenY })
    window.addEventListener('pointerdown', note, true)
    return () => window.removeEventListener('pointerdown', note, true)
  }, [])

  const run = useCallback(
    async (name: string, extra?: Record<string, unknown>) => {
      const answer = await invoke<string | null>('panel.command', { name, ...at.current, ...extra })
      if (answer) setNotice(answer)
      await load()
    },
    [load],
  )

  if (!state) return <div className="panel-loading">…</div>

  return (
    <div className="panel">
      <header className="panel-head">
        <span className="panel-title">WinMirroring</span>
        <button className="btn btn-ghost btn-icon" onClick={() => void run('close')}>✕</button>
      </header>

      <div className="panel-body">
        {state.views.length > 0 && (
          <>
            <h3>На экране</h3>
            {state.localActive ? (
              <div className="current">● Рабочий стол этого устройства</div>
            ) : (
              <button className="item" onClick={() => void run('showLocal')}>Рабочий стол этого устройства</button>
            )}
            {state.views.map((view) =>
              view.active ? (
                <div key={view.streamId}>
                  <div className="current">● {view.title}</div>
                  {view.inputAllowed ? (
                    <div className="segment">
                      <button
                        className={`item${view.inputOn ? ' is-on' : ''}`}
                        onClick={() => void run('viewInput', { streamId: view.streamId, on: true })}
                      >
                        Управлять
                      </button>
                      <button
                        className={`item${view.inputOn ? '' : ' is-on'}`}
                        onClick={() => void run('viewInput', { streamId: view.streamId, on: false })}
                      >
                        Только смотреть
                      </button>
                    </div>
                  ) : (
                    <p className="note">Только просмотр: управление запрещено на том устройстве.</p>
                  )}
                  {(view.kind === 'monitor' || view.kind === 'display') && (
                    <>
                      <button
                        className={`item${state.bar.visible ? ' is-on' : ''}`}
                        onClick={() => void run('barVisible', { on: !state.bar.visible })}
                      >
                        Панель кнопок внизу
                      </button>
                      {state.bar.visible &&
                        state.bar.presets.length > 1 &&
                        state.bar.presets.map((preset) => (
                          <button
                            key={preset}
                            className={`item is-sub${preset === state.bar.active ? ' is-on' : ''}`}
                            onClick={() => void run('barPreset', { text: preset })}
                          >
                            Набор «{preset}»
                          </button>
                        ))}
                      <button className="item" onClick={() => void run('editButtons')}>Настроить кнопки…</button>
                    </>
                  )}
                  <button className="item" onClick={() => void run('keyboard', { streamId: view.streamId })}>
                    Экранная клавиатура
                  </button>
                  <button className="item" onClick={() => void run('closeView', { streamId: view.streamId })}>
                    {view.kind === 'display' ? 'Отключить второй монитор' : 'Закрыть трансляцию'}
                  </button>
                </div>
              ) : (
                <button key={view.streamId} className="item" onClick={() => void run('switchView', { streamId: view.streamId })}>
                  {view.title}
                </button>
              ),
            )}
          </>
        )}

        <h3>Устройства</h3>
        {state.devices.length === 0 && <p className="note">Нет устройств на связи.</p>}
        {state.devices.map((device) => (
          <div key={device.id} className="device-block">
            <div className="device-head">{device.name}</div>
            {device.monitors.length > 1 ? (
              device.monitors.map((monitor, index) => (
                <button
                  key={monitor.index}
                  className="item"
                  onClick={() => void run('showScreen', { deviceId: device.id, monitor: monitor.index })}
                >
                  {monitor.primary ? 'Основной экран' : `Монитор ${index + 1}`} <span className="dim">{monitor.label}</span>
                </button>
              ))
            ) : (
              <button className="item" onClick={() => void run('showScreen', { deviceId: device.id, monitor: 0 })}>
                {device.screenOpen ? 'Переключиться на экран' : 'Показать экран'}
              </button>
            )}
            <button className="item" onClick={() => void run('sendWindow', { deviceId: device.id })}>Отправить окно</button>
            <button className="item" onClick={() => void run('openOnDevice', { deviceId: device.id })}>Открыть файл там…</button>
            <button className="item" onClick={() => void run('controlInput', { deviceId: device.id })}>Управлять вводом</button>
            <button
              className={`item${device.secondMonitor ? ' is-on' : ''}`}
              onClick={() => void run('secondMonitor', { deviceId: device.id })}
            >
              Использовать как второй монитор
            </button>
            {device.canBecome !== false && (
              <button className="item" onClick={() => void run('becomeSecondMonitor', { deviceId: device.id })}>
                Стать его вторым монитором
              </button>
            )}
            <div className="segment">
              {(['Automatic', 'Always', 'Never'] as const).map((choice) => (
                <button
                  key={choice}
                  className={`item${device.sound.here === choice ? ' is-on' : ''}`}
                  onClick={() => void run('soundHere', { deviceId: device.id, text: choice.toLowerCase().replace('automatic', 'auto') })}
                >
                  {choice === 'Automatic' ? 'Звук авто' : choice === 'Always' ? 'Всегда' : 'Никогда'}
                </button>
              ))}
            </div>
          </div>
        ))}

        <h3>Звук этого устройства</h3>
        <p className="note">{state.ownSound}</p>

        <h3>Ввод</h3>
        <p className="note">{state.inputFocus}</p>
        <button className={`item${state.input.enabled ? ' is-on' : ''}`} onClick={() => void run('inputEnabled', { on: !state.input.enabled })}>
          Передавать мышь и клавиатуру
        </button>
        <button
          className={`item${state.input.syncKeyboardLayout ? ' is-on' : ''}`}
          onClick={() => void run('syncKeyboardLayout', { on: !state.input.syncKeyboardLayout })}
        >
          Синхронизировать раскладку
        </button>
        <button
          className={`item${state.input.adminControl ? ' is-on' : ''}`}
          onClick={() => void run('adminControl', { on: !state.input.adminControl })}
        >
          Управление окнами администратора
        </button>
        <button className="item" onClick={() => void run('returnCursor')}>Вернуть курсор</button>

        <h3>Прочее</h3>
        <button className="item" onClick={() => void run('openHub')}>Устройства и раскладка…</button>
        <button className="item" onClick={() => void run('openSettings')}>Параметры</button>
        <button className="item" onClick={() => void run('returnWindows')}>Вернуть окна на место</button>
        <button className="item" onClick={() => void run('hideButton')}>Скрыть кнопку меню</button>

        {notice && <p className="note notice">{notice}</p>}
      </div>
    </div>
  )
}
