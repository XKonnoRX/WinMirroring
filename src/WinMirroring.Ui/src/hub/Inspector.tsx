import { Device } from './model'

type Run = (name: string, extra?: { deviceId?: string; monitor?: string | null; text?: string }) => void

/** Everything about one device, and everything that can be done with it, beside the field. */
export function Inspector({
  device,
  placed,
  onClose,
  onCommand,
  onRemove,
}: {
  device: Device
  placed: boolean
  onClose: () => void
  onCommand: Run
  onRemove: () => void
}) {
  const run = (name: string, text?: string) => onCommand(name, { deviceId: device.id, text })
  const sound = device.sound

  return (
    <aside className="inspector">
      <header className="inspector-head">
        <div>
          <h2>{device.name}</h2>
          <span className="inspector-state">
            <span className={`dot${device.online ? ' dot-on' : ''}`} /> {device.state}
            {device.rtt && ` · ${device.rtt}`}
          </span>
        </div>
        <button className="btn btn-ghost btn-icon" onClick={onClose} title="Закрыть">✕</button>
      </header>

      <div className="inspector-body">
        {device.lastError && <div className="inspector-error">{device.lastError}</div>}

        {!device.paired && device.introducedBy ? (
          <section>
            <p className="inspector-note">
              Это устройство сопряжено с «{device.introducedBy}», а «{device.introducedBy}» сопряжено с этим.
              Подтверждать на обоих экранах не нужно: отпечаток пришёл от устройства, которое вы уже подтвердили
              вручную, и оно же передаст нашу сторону обратно.
            </p>
            <dl className="inspector-facts">
              <dt>Отпечаток</dt>
              <dd className="mono">{device.fingerprint || '—'}</dd>
            </dl>
            <button className="btn btn-accent wide" onClick={() => run('trust')}>Доверять</button>
          </section>
        ) : !device.paired ? (
          <section>
            <p className="inspector-note">
              Устройство найдено в сети, но ещё не сопряжено. Запрос уйдёт туда, и там его нужно подтвердить.
            </p>
            <button className="btn btn-accent wide" onClick={() => run('pair')}>Сопрячь</button>
          </section>
        ) : device.isThisDevice ? (
          <section>
            <p className="inspector-note">Это устройство. Перетащите его на поле так, как оно стоит относительно других.</p>
          </section>
        ) : (
          <>
            {/* Ordered by how often each thing is reached for: looking at the device and driving it first, then
                the everyday tools, then what is set up once in a while. */}
            <section>
              <div className="inspector-grid">
                <button className="btn btn-accent" disabled={!device.online} onClick={() => onCommand('showScreen', { deviceId: device.id })}>
                  Показать экран
                </button>
                <button className="btn" disabled={!device.online} onClick={() => run('controlInput')}>
                  Управлять вводом
                </button>
                {/* Only where it can do something: the device is not connected, and the address of its network
                    adapter was seen while it was. The packet is not answered: the device turning up is the answer. */}
                {device.canWake && (
                  <button className="btn wide" onClick={() => run('wake')}>Разбудить по сети</button>
                )}
              </div>
            </section>

            <section>
              <h3>Инструменты</h3>
              <div className="inspector-grid">
                <button className="btn" disabled={!device.host} onClick={() => run('openShare')}>Сетевая папка</button>
                <button className="btn" disabled={!device.online} onClick={() => run('openOnDevice')}>Открыть файл там…</button>
                {/* Commands run on the device itself, over the session it already has — in the place of an SSH
                    login. It needs no address, only a live session, which is why this one goes by `online`. */}
                <button className="btn" disabled={!device.online} onClick={() => run('console')}>Консоль</button>
                {/* The same console with administrator rights. It needs its own setting turned on over there, is
                    confirmed there every time, and Windows asks there as well — so it only works on a device
                    somebody is sitting at. */}
                <button className="btn" disabled={!device.online} onClick={() => run('consoleElevated')}>
                  Консоль администратора
                </button>
                <button className="btn" disabled={!device.host} onClick={() => run('remoteDesktop')}>Рабочий стол (RDP)</button>
                <button className="btn" disabled={!device.host} onClick={() => run('copyAddress')}>Копировать IP</button>
                <button className="btn" disabled={!device.online} onClick={() => run('latency')}>Проверить задержку</button>
                <button className="btn" disabled={!device.host} onClick={() => run('ping')}>Ping в консоли</button>
                <button className="btn" disabled={!device.online} onClick={() => run('peerLog')}>Забрать журнал</button>
                <button className="btn" disabled={!device.online} onClick={() => run('reconnect')}>Переподключить</button>
              </div>
              {!device.host && (
                <p className="inspector-note">
                  Адрес этого устройства пока неизвестен — оно ещё не отвечало в сети, поэтому папку, RDP и ping
                  открыть не по чему.
                </p>
              )}
            </section>

            <section>
              <h3>Окна и мониторы</h3>
              <div className="inspector-grid">
                <button className="btn" disabled={!device.online} onClick={() => run('sendWindow')}>Отправить окно</button>
                <button className="btn" disabled={!device.online} onClick={() => run('secondMonitor')}>
                  Сделать вторым монитором
                </button>
                {device.hasVirtualDisplay !== false && (
                  <button className="btn" disabled={!device.online} onClick={() => run('becomeSecondMonitor')}>
                    Стать его вторым монитором
                  </button>
                )}
              </div>
              {device.hasVirtualDisplay === false && (
                <p className="inspector-note">
                  Стать вторым монитором {device.name} нельзя: там нет драйвера виртуального монитора.
                </p>
              )}
            </section>

            {sound && (
              <section>
                <h3>Звук {device.name} здесь</h3>
                <div className="segment">
                  {(['auto', 'always', 'never'] as const).map((value) => (
                    <button
                      key={value}
                      className={`btn${sound.here === value ? ' btn-accent' : ''}`}
                      onClick={() => run('soundHere', value)}
                    >
                      {value === 'auto' ? 'Авто' : value === 'always' ? 'Всегда' : 'Никогда'}
                    </button>
                  ))}
                </div>
                {sound.playing && <p className="inspector-note">Сейчас играет здесь.</p>}
                <h3>Звук этого устройства</h3>
                <div className="segment">
                  <button className={`btn${sound.sentTo ? ' btn-accent' : ''}`} onClick={() => run('soundTo', 'pin')}>
                    Туда
                  </button>
                  <button className="btn" onClick={() => run('soundTo', 'auto')}>Авто</button>
                  <button className="btn" onClick={() => run('soundTo', 'here')}>Здесь</button>
                </div>
                <p className="inspector-note">
                  «Туда» — всегда на {device.name}; «Авто» — за вводом и просмотром; «Здесь» — никуда не передавать.
                </p>
              </section>
            )}

            <section>
              <h3>Устройство</h3>
              {device.canUpdate && (
                <button className="btn btn-accent wide" onClick={() => run('updateDevice')}>
                  Обновить отсюда
                </button>
              )}
              <dl className="facts">
                <dt>Версия</dt>
                <dd>{device.version ?? 'неизвестна'}</dd>
                <dt>Адрес</dt>
                <dd>{device.address || '—'}</dd>
                <dt>Отпечаток</dt>
                <dd className="mono">{device.fingerprint || '—'}</dd>
              </dl>
              <div className="inspector-row">
                {placed && <button className="btn wide" onClick={onRemove}>Убрать с поля</button>}
                <button className="btn wide danger" onClick={() => run('unpair')}>Разорвать сопряжение</button>
              </div>
            </section>
          </>
        )}
      </div>
    </aside>
  )
}
