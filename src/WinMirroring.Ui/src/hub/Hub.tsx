import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { invoke, on } from '../bridge'
import {
  Cell,
  Device,
  Group,
  Hub as HubState,
  anchorOf,
  cellKey,
  cellsOf,
  groupOf,
  neighbourOffsets,
  sameCells,
} from './model'
import { Inspector } from './Inspector'
import { Tray } from './Tray'
import './Hub.css'

// Cells sit far enough apart that the outline around a device and the seam between two of them both have room.
export const PITCH_X = 292
export const PITCH_Y = 204
export const CARD_W = 228
export const CARD_H = 150

const MIN_ZOOM = 0.3
const MAX_ZOOM = 1.8
const TRAY_H = 164

type View = { x: number; y: number; zoom: number }
type Anchor = { column: number; row: number }

type Drag = {
  group: Group
  x: number
  y: number
  grabX: number
  grabY: number
  moved: boolean
  fromTray: boolean
}

export function Hub() {
  const surface = useRef<HTMLDivElement>(null)
  const [hub, setHub] = useState<HubState | null>(null)
  const [cells, setCells] = useState<Cell[]>([])
  const [saved, setSaved] = useState<Cell[]>([])
  const [view, setView] = useState<View>({ x: 0, y: 0, zoom: 1 })
  const [drag, setDrag] = useState<Drag | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const groups = useMemo(() => (hub?.devices ?? []).map(groupOf), [hub])
  const anchors = useMemo(() => {
    const map = new Map<string, Anchor>()
    for (const group of groups) {
      const anchor = anchorOf(group, cells)
      if (anchor) map.set(group.device.id, anchor)
    }
    return map
  }, [groups, cells])

  /** Which device owns each cell, so neighbours and free space are known at a glance. */
  const owners = useMemo(() => {
    const map = new Map<string, string>()
    for (const group of groups) {
      const anchor = anchors.get(group.device.id)
      if (!anchor) continue
      for (const pane of group.panes) {
        map.set(cellKey(anchor.column + pane.dc, anchor.row + pane.dr), group.device.id)
      }
    }
    return map
  }, [groups, anchors])

  const placedGroups = useMemo(() => groups.filter((g) => anchors.has(g.device.id)), [groups, anchors])
  const unplaced = useMemo(
    () => groups.filter((g) => !anchors.has(g.device.id) && g.device.paired),
    [groups, anchors],
  )
  const discovered = useMemo(() => groups.map((g) => g.device).filter((d) => !d.paired), [groups])
  const dirty = useMemo(() => !sameCells(cells, saved), [cells, saved])
  const selectedDevice = useMemo(() => hub?.devices.find((d) => d.id === selected) ?? null, [hub, selected])

  // ---- loading ------------------------------------------------------------------------------------------

  const load = useCallback(async (recentre: boolean) => {
    const state = await invoke<HubState>('hub.get')
    setHub(state)
    setCells((current) => (recentre || current.length === 0 ? state.cells : current))
    setSaved(state.cells)
    if (recentre) fitTo(state.cells)
  }, [])

  const fitTo = useCallback((list: Cell[]) => {
    const box = surface.current?.getBoundingClientRect()
    if (!box) return
    if (list.length === 0) {
      setView({ x: box.width / 2, y: (box.height - TRAY_H) / 2, zoom: 1 })
      return
    }

    const left = Math.min(...list.map((c) => c.column)) * PITCH_X - CARD_W / 2
    const right = Math.max(...list.map((c) => c.column)) * PITCH_X + CARD_W / 2
    const top = Math.min(...list.map((c) => c.row)) * PITCH_Y - CARD_H / 2
    const bottom = Math.max(...list.map((c) => c.row)) * PITCH_Y + CARD_H / 2
    const margin = 80
    const zoom = clamp(
      Math.min((box.width - margin * 2) / (right - left), (box.height - TRAY_H - margin) / (bottom - top), 1.1),
      MIN_ZOOM,
      MAX_ZOOM,
    )
    setView({
      x: box.width / 2 - ((left + right) / 2) * zoom,
      y: (box.height - TRAY_H) / 2 - ((top + bottom) / 2) * zoom,
      zoom,
    })
  }, [])

  useEffect(() => {
    void load(true)
    return on('hub.changed', () => void load(false))
  }, [load])

  // The first snapshot can arrive before the field has its real size; centre it once it does.
  const fitted = useRef(false)
  useEffect(() => {
    const element = surface.current
    if (!element) return
    const observer = new ResizeObserver(() => {
      if (!fitted.current && element.clientWidth > 0 && hub) {
        fitted.current = true
        fitTo(cells)
      }
    })

    observer.observe(element)
    return () => observer.disconnect()
  }, [hub, cells, fitTo])

  // ---- commands -----------------------------------------------------------------------------------------

  const run = useCallback(async (name: string, extra?: { deviceId?: string; monitor?: string | null; text?: string }) => {
    try {
      const answer = await invoke<string | null>('hub.command', { name, ...extra })
      if (answer) setStatus(answer)
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'Не получилось.')
    }
  }, [])

  const save = async () => {
    setBusy(true)
    try {
      setStatus(await invoke<string>('hub.save', { cells }))
      setSaved(cells)
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'Не удалось сохранить.')
    } finally {
      setBusy(false)
    }
  }

  // ---- the field ----------------------------------------------------------------------------------------

  const toSurface = useCallback((clientX: number, clientY: number) => {
    const box = surface.current!.getBoundingClientRect()
    return { x: clientX - box.left, y: clientY - box.top }
  }, [])

  /**
   * Where the dragged device's first cell is being held, in field coordinates: the cursor, less the point inside
   * the card that was taken hold of, plus half a card — because a cell is placed by its centre. Both the carried
   * card and the outline of where it would land come from this one point, so they cannot drift apart, and the grab
   * is in field units while the cursor is in pixels on screen, so the zoom belongs on exactly one of them.
   */
  const heldAt = useMemo(() => {
    if (!drag) return null
    return {
      x: (drag.x - view.x) / view.zoom - drag.grabX + CARD_W / 2,
      y: (drag.y - view.y) / view.zoom - drag.grabY + CARD_H / 2,
    }
  }, [drag, view])

  /** The same point back in pixels on the field, which is what the carried card is positioned by. */
  const heldOnScreen = useMemo(() => {
    if (!heldAt) return null
    return { x: view.x + heldAt.x * view.zoom, y: view.y + heldAt.y * view.zoom }
  }, [heldAt, view])

  const hoveredAnchor = useMemo<Anchor | null>(() => {
    if (!heldAt || !heldOnScreen || !surface.current) return null
    const box = surface.current.getBoundingClientRect()
    if (heldOnScreen.y > box.height - TRAY_H) return null
    return {
      column: Math.round(heldAt.x / PITCH_X),
      row: Math.round(heldAt.y / PITCH_Y),
    }
  }, [heldAt, heldOnScreen])

  /** A device may stand here only if none of its monitors would land on another device. */
  const canDrop = useMemo(() => {
    if (!drag || !hoveredAnchor) return false
    return drag.group.panes.every((pane) => {
      const owner = owners.get(cellKey(hoveredAnchor.column + pane.dc, hoveredAnchor.row + pane.dr))
      return owner === undefined || owner === drag.group.device.id
    })
  }, [drag, hoveredAnchor, owners])

  const panning = useRef<{ x: number; y: number; view: View } | null>(null)

  // Every finger on the field, so two of them can pinch (a card may swallow the second one, hence the capture phase).
  const pointers = useRef(new Map<number, { x: number; y: number }>())
  const pinch = useRef<{ distance: number; x: number; y: number; view: View } | null>(null)

  const onPointerDownCapture = (event: React.PointerEvent) => {
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (pointers.current.size === 2) {
      // A second finger: stop moving anything and start zooming instead.
      panning.current = null
      setDrag(null)
      pinch.current = { ...measure(), view }
    }
  }

  const measure = () => {
    const [a, b] = [...pointers.current.values()]
    if (!a || !b) return { distance: 1, x: 0, y: 0 }
    return {
      distance: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)),
      x: (a.x + b.x) / 2,
      y: (a.y + b.y) / 2,
    }
  }

  const onSurfaceDown = (event: React.PointerEvent) => {
    if (event.button !== 0 && event.button !== 1) return
    panning.current = { x: event.clientX, y: event.clientY, view }
    setSelected(null)
    ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  }

  const onSurfaceMove = (event: React.PointerEvent) => {
    if (pointers.current.has(event.pointerId)) {
      pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    }

    // Two fingers: the distance between them is the zoom, and the point between them is held in place.
    if (pinch.current && pointers.current.size >= 2) {
      const start = pinch.current
      const now = measure()
      const zoom = clamp((start.view.zoom * now.distance) / start.distance, MIN_ZOOM, MAX_ZOOM)
      const box = surface.current!.getBoundingClientRect()
      const anchor = { x: start.x - box.left, y: start.y - box.top }
      const world = { x: (anchor.x - start.view.x) / start.view.zoom, y: (anchor.y - start.view.y) / start.view.zoom }
      setView({
        x: now.x - box.left - world.x * zoom,
        y: now.y - box.top - world.y * zoom,
        zoom,
      })
      return
    }

    if (drag) {
      const point = toSurface(event.clientX, event.clientY)
      setDrag({ ...drag, x: point.x, y: point.y, moved: true })
      return
    }

    const pan = panning.current
    if (!pan) return
    setView({ ...pan.view, x: pan.view.x + (event.clientX - pan.x), y: pan.view.y + (event.clientY - pan.y) })
  }

  const onSurfaceUp = (event: React.PointerEvent) => {
    pointers.current.delete(event.pointerId)
    if (pointers.current.size < 2) {
      pinch.current = null
    }

    panning.current = null
    if (!drag) {
      return
    }

    if (!drag.moved) {
      setSelected(drag.group.device.id)
    } else if (pinch.current) {
      // The drag turned into a pinch: leave the device where it was.
    } else if (hoveredAnchor && canDrop) {
      place(drag.group, hoveredAnchor)
    } else if (!hoveredAnchor && !drag.fromTray) {
      setCells((current) => current.filter((c) => c.deviceId !== drag.group.device.id))
    }

    setDrag(null)
    try {
      ;(event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId)
    } catch {
      // the pointer is already gone
    }
  }

  const onWheel = (event: React.WheelEvent) => {
    const point = toSurface(event.clientX, event.clientY)
    const world = { x: (point.x - view.x) / view.zoom, y: (point.y - view.y) / view.zoom }
    const zoom = clamp(view.zoom * (event.deltaY < 0 ? 1.12 : 1 / 1.12), MIN_ZOOM, MAX_ZOOM)
    setView({ x: point.x - world.x * zoom, y: point.y - world.y * zoom, zoom })
  }

  const zoomBy = (factor: number) => {
    const box = surface.current?.getBoundingClientRect()
    if (!box) return
    const zoom = clamp(view.zoom * factor, MIN_ZOOM, MAX_ZOOM)
    const centre = { x: box.width / 2, y: (box.height - TRAY_H) / 2 }
    setView({
      x: centre.x - ((centre.x - view.x) / view.zoom) * zoom,
      y: centre.y - ((centre.y - view.y) / view.zoom) * zoom,
      zoom,
    })
  }

  const startDrag = (group: Group, event: React.PointerEvent, fromTray: boolean) => {
    event.stopPropagation()
    const card = (event.currentTarget as HTMLElement).getBoundingClientRect()
    const point = toSurface(event.clientX, event.clientY)
    setSelected(group.device.id)
    setDrag({
      group,
      x: point.x,
      y: point.y,
      grabX: fromTray ? CARD_W / 2 : (event.clientX - card.left) / view.zoom,
      grabY: fromTray ? CARD_H / 2 : (event.clientY - card.top) / view.zoom,
      moved: false,
      fromTray,
    })
    ;(surface.current as HTMLElement).setPointerCapture(event.pointerId)
  }

  const place = (group: Group, anchor: Anchor) => {
    setCells((current) => [...current.filter((c) => c.deviceId !== group.device.id), ...cellsOf(group, anchor)])
    setStatus(null)
  }

  const thisDevicePlaced = placedGroups.some((g) => g.device.isThisDevice)

  if (!hub) {
    return <div className="hub-loading">Загрузка…</div>
  }

  return (
    <div className="hub">
      <header className="hub-head">
        <div className="hub-title">
          <h1>Устройства</h1>
          <span className="hub-sub">{hub.inputFocus} · {hub.ownSound}</span>
        </div>
        <div className="hub-actions">
          <button className="btn btn-ghost btn-icon" onClick={() => zoomBy(1 / 1.25)} title="Отдалить">−</button>
          <button className="btn btn-ghost btn-icon" onClick={() => zoomBy(1.25)} title="Приблизить">+</button>
          <button className="btn btn-ghost" onClick={() => fitTo(cells)}>По центру</button>
          <button className="btn btn-ghost" onClick={() => void run('openSettings')}>Параметры</button>
          <button className="btn" onClick={() => setCells(saved)} disabled={!dirty || busy}>Отменить</button>
          <button className="btn btn-accent" onClick={() => void save()} disabled={!dirty || busy}>
            {busy ? 'Сохраняю…' : 'Сохранить раскладку'}
          </button>
        </div>
      </header>

      <div className="hub-body">
        <div
          ref={surface}
          className={`hub-field${drag ? ' is-dragging' : ''}`}
          onPointerDownCapture={onPointerDownCapture}
          onPointerDown={onSurfaceDown}
          onPointerMove={onSurfaceMove}
          onPointerUp={onSurfaceUp}
          onPointerCancel={onSurfaceUp}
          onWheel={onWheel}
        >
          <div
            className="hub-grid"
            style={{
              backgroundSize: `${PITCH_X * view.zoom}px ${PITCH_Y * view.zoom}px`,
              backgroundPosition: `${view.x - (PITCH_X / 2) * view.zoom}px ${view.y - (PITCH_Y / 2) * view.zoom}px`,
            }}
          />

          <div className="hub-world" style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.zoom})` }}>
            {drag && hoveredAnchor && (
              <div className={`hub-slot${canDrop ? '' : ' is-blocked'}`} style={groupBox(drag.group, hoveredAnchor)}>
                {drag.group.panes.map((pane) => (
                  <div key={pane.key ?? 'whole'} className="hub-slot-pane" style={paneBox(pane.dc, pane.dr)} />
                ))}
              </div>
            )}

            {seams(placedGroups, anchors, owners).map((seam) => (
              <div key={seam.key} className={`hub-seam ${seam.horizontal ? 'is-h' : 'is-v'}`} style={seam.style} />
            ))}

            {placedGroups.map((group) => {
              const anchor = anchors.get(group.device.id)!
              if (drag?.group.device.id === group.device.id && drag.moved) return null
              return (
                <DeviceGroup
                  key={group.device.id}
                  group={group}
                  style={groupBox(group, anchor)}
                  selected={selected === group.device.id}
                  lonely={isLonely(group, anchor, owners)}
                  onPointerDown={(e) => startDrag(group, e, false)}
                  onOpenScreen={(monitor) => void run('showScreen', { deviceId: group.device.id, monitor })}
                />
              )
            })}
          </div>

          {drag && drag.moved && heldOnScreen && (
            <div
              className="hub-floating"
              style={{
                // The card inside is drawn around this point, not from it, the same way a placed one is.
                left: heldOnScreen.x,
                top: heldOnScreen.y,
                transform: `scale(${view.zoom})`,
              }}
            >
              <DeviceGroup group={drag.group} style={groupBox(drag.group, { column: 0, row: 0 }, true)} selected lonely={false} />
            </div>
          )}

          <Tray
            unplaced={unplaced}
            discovered={discovered}
            addresses={hub.manualAddresses}
            dragging={drag !== null && !drag.fromTray}
            onDragDevice={(group, event) => startDrag(group, event, true)}
            onCommand={run}
          />
        </div>

        {selectedDevice && (
          <Inspector
            device={selectedDevice}
            placed={anchors.has(selectedDevice.id)}
            onClose={() => setSelected(null)}
            onCommand={run}
            onRemove={() => setCells((current) => current.filter((c) => c.deviceId !== selectedDevice.id))}
          />
        )}
      </div>

      <footer className="hub-foot">
        {!thisDevicePlaced && <span className="hub-warn">Это устройство не на поле: его курсор никуда не перейдёт.</span>}
        {status && <span className="hub-status">{status}</span>}
        {dirty && !status && <span className="hub-status">Есть несохранённые изменения.</span>}
        <span className="hub-net">{hub.network}</span>
      </footer>
    </div>
  )
}

// ---- a device on the field --------------------------------------------------------------------------------

function DeviceGroup({
  group,
  style,
  selected,
  lonely,
  onPointerDown,
  onOpenScreen,
}: {
  group: Group
  style: React.CSSProperties
  selected: boolean
  lonely: boolean
  onPointerDown?: (event: React.PointerEvent) => void
  onOpenScreen?: (monitor: string | null) => void
}) {
  const device = group.device
  return (
    <div
      className={`device${device.isThisDevice ? ' is-self' : ''}${device.online ? '' : ' is-offline'}${
        selected ? ' is-selected' : ''
      }`}
      style={style}
      onPointerDown={onPointerDown}
    >
      <div className="device-bar">
        <span className={`dot${device.online ? ' dot-on' : ''}`} />
        <span className="device-name">{device.name}</span>
        {device.isThisDevice && <span className="chip chip-accent">здесь</span>}
        {!device.isThisDevice && !device.online && <span className="chip">не на связи</span>}
        {lonely && <span className="chip">отдельно</span>}
      </div>
      {group.panes.map((pane) => (
        <div key={pane.key ?? 'whole'} className="device-pane" style={paneBox(pane.dc, pane.dr)}>
          <div className="pane-screen" style={{ aspectRatio: String(pane.aspect) }}>
            <div className="pane-glow" />
            <div className="pane-label">{pane.label}</div>
            {pane.size && <div className="pane-size">{pane.size}</div>}
            {onOpenScreen && !device.isThisDevice && device.online && (
              <button
                className={`pane-open${device.shownMonitor === pane.key ? ' is-shown' : ''}`}
                onPointerDown={(e) => e.stopPropagation()}
                onClick={() => onOpenScreen(pane.key)}
                title="Показать этот экран здесь"
              >
                {device.shownMonitor === pane.key ? 'Показан' : 'Показать'}
              </button>
            )}
          </div>
        </div>
      ))}
    </div>
  )
}

// ---- geometry ---------------------------------------------------------------------------------------------

function groupBox(group: Group, anchor: Anchor, atOrigin = false): React.CSSProperties {
  const left = (atOrigin ? 0 : anchor.column * PITCH_X) - CARD_W / 2
  const top = (atOrigin ? 0 : anchor.row * PITCH_Y) - CARD_H / 2
  return {
    left,
    top,
    width: (group.columns - 1) * PITCH_X + CARD_W,
    height: (group.rows - 1) * PITCH_Y + CARD_H,
  }
}

function paneBox(dc: number, dr: number): React.CSSProperties {
  return { left: dc * PITCH_X, top: dr * PITCH_Y, width: CARD_W, height: CARD_H }
}

function isLonely(group: Group, anchor: Anchor, owners: Map<string, string>): boolean {
  return !group.panes.some((pane) =>
    neighbourOffsets.some((o) => {
      const owner = owners.get(cellKey(anchor.column + pane.dc + o.dc, anchor.row + pane.dr + o.dr))
      return owner !== undefined && owner !== group.device.id
    }),
  )
}

/** The glowing seams: where the cursor crosses from one device to another. */
function seams(groups: Group[], anchors: Map<string, Anchor>, owners: Map<string, string>) {
  const found: { key: string; horizontal: boolean; style: React.CSSProperties }[] = []
  for (const group of groups) {
    const anchor = anchors.get(group.device.id)
    if (!anchor) continue
    for (const pane of group.panes) {
      const column = anchor.column + pane.dc
      const row = anchor.row + pane.dr
      const right = owners.get(cellKey(column + 1, row))
      if (right !== undefined && right !== group.device.id) {
        found.push({
          key: `h${column},${row}`,
          horizontal: true,
          style: { left: column * PITCH_X + CARD_W / 2, top: row * PITCH_Y - 1.5, width: PITCH_X - CARD_W },
        })
      }

      const below = owners.get(cellKey(column, row + 1))
      if (below !== undefined && below !== group.device.id) {
        found.push({
          key: `v${column},${row}`,
          horizontal: false,
          style: { left: column * PITCH_X - 1.5, top: row * PITCH_Y + CARD_H / 2, height: PITCH_Y - CARD_H },
        })
      }
    }
  }

  return found
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max)
}

export type { Device }
