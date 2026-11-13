import { useState } from 'react'
import { Device, Group } from './model'

type Run = (name: string, extra?: { deviceId?: string; monitor?: string | null; text?: string }) => void

/**
 * Along the bottom: the paired devices that are not on the field yet, whatever was just found nearby, and the
 * box for a device that broadcast cannot reach (through a VPN, say).
 */
export function Tray({
  unplaced,
  discovered,
  addresses,
  dragging,
  onDragDevice,
  onCommand,
}: {
  unplaced: Group[]
  discovered: Device[]
  addresses: string[]
  dragging: boolean
  onDragDevice: (group: Group, event: React.PointerEvent) => void
  onCommand: Run
}) {
  const [address, setAddress] = useState('')
  const [adding, setAdding] = useState(false)
  const empty = unplaced.length === 0 && discovered.length === 0

  // A device found on the network is sought out and confirmed on both screens; one a paired device vouched for
  // needs only a press here, so the two are offered apart.
  const nearby = discovered.filter((d) => !d.introducedBy)
  const introduced = discovered.filter((d) => d.introducedBy)

  const add = () => {
    const text = address.trim()
    if (!text) return
    onCommand('addAddress', { text })
    setAddress('')
    setAdding(false)
  }

  return (
    <footer className={`tray${dragging ? ' is-target' : ''}${empty && !adding && !dragging ? ' is-empty' : ''}`}>
      <div className="tray-head">
        <span className="tray-title">
          {dragging ? 'Отпустите здесь, чтобы убрать устройство с поля' : empty ? 'Все устройства на поле' : 'Не на поле'}
        </span>
        {adding ? (
          <span className="tray-add">
            <input
              autoFocus
              className="field"
              placeholder="имя или IP, можно host:47620"
              value={address}
              onChange={(e) => setAddress(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') add()
                if (e.key === 'Escape') setAdding(false)
              }}
            />
            <button className="btn btn-accent" onClick={add}>Добавить</button>
            <button className="btn btn-ghost" onClick={() => setAdding(false)}>Отмена</button>
          </span>
        ) : (
          <button className="btn btn-ghost" onClick={() => setAdding(true)}>+ Добавить устройство вручную</button>
        )}
      </div>

      <div className="tray-items">
        {unplaced.map((group) => (
          <button
            key={group.device.id}
            className={`tray-card${group.device.isThisDevice ? ' is-self' : ''}`}
            onPointerDown={(e) => onDragDevice(group, e)}
          >
            <span className="tray-card-name">{group.device.name}</span>
            <span className="tray-card-note">
              {group.panes.length > 1 ? `${group.panes.length} монитора` : group.device.isThisDevice ? 'это устройство' : group.device.state}
            </span>
          </button>
        ))}

        {nearby.length > 0 && <span className="tray-split">Найдены рядом</span>}
        {nearby.map((device) => (
          <div key={device.id} className="tray-card is-found">
            <span className="tray-card-name">{device.name}</span>
            <button className="btn btn-accent tray-pair" onClick={() => onCommand('pair', { deviceId: device.id })}>
              Сопрячь
            </button>
          </div>
        ))}

        {introduced.length > 0 && <span className="tray-split">Через другие устройства</span>}
        {introduced.map((device) => (
          <div key={device.id} className="tray-card is-found is-introduced">
            <span className="tray-card-name">{device.name}</span>
            <span className="tray-card-note">через {device.introducedBy}</span>
            <button className="btn btn-accent tray-pair" onClick={() => onCommand('trust', { deviceId: device.id })}>
              Доверять
            </button>
          </div>
        ))}

        {addresses.length > 0 && <span className="tray-split">Адреса вручную</span>}
        {addresses.map((item) => (
          <div key={item} className="tray-address">
            <span className="mono">{item}</span>
            <button className="btn btn-ghost btn-icon" title="Убрать" onClick={() => onCommand('removeAddress', { text: item })}>
              ✕
            </button>
          </div>
        ))}
      </div>
    </footer>
  )
}
