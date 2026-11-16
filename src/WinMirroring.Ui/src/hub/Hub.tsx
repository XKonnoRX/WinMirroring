import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { invoke, on } from '../bridge'
import {
  Device,
  Group,
  Hub as HubState,
  Placement,
  Point,
  Rect,
  groupOf,
  isAlone,
  placementsFromCells,
  rectsAt,
  samePlacements,
  seamsBetween,
  settle,
  unionOf,
} from './model'
import { Inspector } from './Inspector'
import { Tray } from './Tray'
import './Hub.css'

/**
 * Field pixels (the devices' own desktop pixels) to page pixels at zoom 1. Monitors are drawn at their real sizes
 * and where they really are, so a 1920×1080 monitor is about 210 page pixels wide.
 */
export const SCALE = 0.11

const MIN_ZOOM = 0.25
const MAX_ZOOM = 2.5
const TRAY_H = 164

/** How close, in page pixels on the screen, an edge has to come to another to be drawn onto it. */
const SNAP_PX = 14

/**
 * How far a press has to travel before it counts as carrying a device rather than just picking it. Until it does,
 * a press only opens the panel beside the field: the field does not go into its moving state, the card does not
 * lift off and the tray does not turn into a place to drop things. Without it every single tap did all of that
 * for the instant between pressing and letting go, which is nothing but a twitch.
 *
 * A finger is given more room than a mouse because it wobbles more; a couple of pixels of mouse movement is
 * already deliberate, a couple of pixels of finger movement is not.
 */
const dragStartsAfter = (pointerType: string) => (pointerType === 'touch' ? 8 : 3)

type View = { x: number; y: number; zoom: number }

type Drag = {
  group: Group
  /** The pointer, on the field element. */
  x: number
  y: number
  /** Where the device was taken hold of: the pointer's offset from the device's origin, in field pixels. */
  grab: Point

  /** Where the press began, to measure how far it has travelled since.  */
  fromX: number
  fromY: number
  threshold: number

  /** The press has travelled far enough to be a move rather than a pick. */
  moved: boolean
  fromTray: boolean
}

export function Hub() {
  const surface = useRef<HTMLDivElement>(null)
  const [hub, setHub] = useState<HubState | null>(null)
  const [places, setPlaces] = useState<Placement[]>([])
  const [saved, setSaved] = useState<Placement[]>([])
  const [view, setView] = useState<View>({ x: 0, y: 0, zoom: 1 })
  const [drag, setDrag] = useState<Drag | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const groups = useMemo(() => (hub?.devices ?? []).map(groupOf), [hub])
  const groupById = useMemo(() => new Map(groups.map((g) => [g.device.id, g])), [groups])
  const placeOf = useMemo(() => new Map(places.map((p) => [p.deviceId, { x: p.x, y: p.y }])), [places])

  const placedGroups = useMemo(() => groups.filter((g) => placeOf.has(g.device.id)), [groups, placeOf])
  const unplaced = useMemo(() => groups.filter((g) => !placeOf.has(g.device.id) && g.device.paired), [groups, placeOf])
  const discovered = useMemo(() => groups.map((g) => g.device).filter((d) => !d.paired), [groups])
  const dirty = useMemo(() => !samePlacements(places, saved), [places, saved])
  const selectedDevice = useMemo(() => hub?.devices.find((d) => d.id === selected) ?? null, [hub, selected])

  // ---- loading ------------------------------------------------------------------------------------------

  /**
   * What the app last told us the layout is, or what we last saved — the same thing as `saved`, kept in a ref so
   * that a state updater can read it. It has to be read before the new snapshot replaces it, because an updater
   * runs later than the call that queues it.
   */
  const savedRef = useRef<Placement[]>([])
  const remember = useCallback((list: Placement[]) => {
    savedRef.current = list
    setSaved(list)
  }, [])

  const fitTo = useCallback((list: Placement[], all: Group[]) => {
    const box = surface.current?.getBoundingClientRect()
    if (!box) return
    const byId = new Map(all.map((g) => [g.device.id, g]))
    const rects = list.flatMap((p) => {
      const group = byId.get(p.deviceId)
      return group ? rectsAt(group, p) : []
    })
    if (rects.length === 0) {
      setView({ x: box.width / 2, y: (box.height - TRAY_H) / 2, zoom: 1 })
      return
    }

    const bounds = unionOf(rects)
    const left = bounds.left * SCALE
    const right = bounds.right * SCALE
    const top = bounds.top * SCALE - 40 // the names above the devices
    const bottom = bounds.bottom * SCALE
    const margin = 70
    const zoom = clamp(
      Math.min((box.width - margin * 2) / (right - left), (box.height - TRAY_H - margin) / (bottom - top), 1.6),
      MIN_ZOOM,
      MAX_ZOOM,
    )
    setView({
      x: box.width / 2 - ((left + right) / 2) * zoom,
      y: (box.height - TRAY_H) / 2 - ((top + bottom) / 2) * zoom,
      zoom,
    })
  }, [])

  const load = useCallback(
    async (recentre: boolean) => {
      const state = await invoke<HubState>('hub.get')
      const wasSaved = savedRef.current
      const all = state.devices.map(groupOf)

      // A layout from before devices stood freely has only its grid; it is shown worked out into places, and
      // becomes a free one when it is saved from here.
      const fresh = state.placements.length > 0 ? state.placements : placementsFromCells(all, state.cells)
      setHub(state)

      // A layout changed on another device arrives here as an ordinary update, and this is where it used to stop:
      // the field kept whatever it was showing, so the new arrangement only appeared when the window was opened
      // again. Now it follows, unless this field is mid-edit, in which case what is on the screen is the user's
      // own unsaved arrangement and must not be pulled out from under them. "Отменить" then takes the new one.
      setPlaces((current) => (recentre || current.length === 0 || samePlacements(current, wasSaved) ? fresh : current))
      remember(fresh)
      if (recentre) fitTo(fresh, all)
    },
    [remember, fitTo],
  )

  useEffect(() => {
    void load(true)
    return on('hub.changed', () => void load(false))
  }, [load])

  // The first snapshot can arrive before the field has its real size; centre it once it does.
  const fitted = useRef(false)
  useEffect(() => {
    const element = surface.current
    if (!element || !hub || fitted.current) return
    const fit = () => {
      if (!fitted.current && element.clientWidth > 0) {
        fitted.current = true
        fitTo(places, groups)
      }
    }

    // At once if the field already has its size; otherwise when it gets one.
    fit()
    const observer = new ResizeObserver(fit)
    observer.observe(element)
    return () => observer.disconnect()
  }, [hub, places, groups, fitTo])

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
      setStatus(await invoke<string>('hub.save', { placements: places }))
      remember(places)
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

  /** A point on the field element, in field pixels. */
  const toField = useCallback(
    (x: number, y: number): Point => ({ x: (x - view.x) / view.zoom / SCALE, y: (y - view.y) / view.zoom / SCALE }),
    [view],
  )

  /** Over the tray: letting go there takes the device off the field. */
  const isOverTray = useCallback((d: Drag) => {
    if (!surface.current) return false
    return d.y > surface.current.getBoundingClientRect().height - TRAY_H
  }, [])
  const overTray = useMemo(() => (drag ? isOverTray(drag) : false), [drag, isOverTray])

  /** The monitors of every device except the one being carried, which is what the carried one settles against. */
  const otherRects = useMemo(() => {
    const rects: Rect[] = []
    for (const group of placedGroups) {
      if (group.device.id === drag?.group.device.id) continue
      rects.push(...rectsAt(group, placeOf.get(group.device.id)!))
    }
    return rects
  }, [placedGroups, placeOf, drag?.group.device.id])

  /** Where the carried device would land if let go now: snapped to its neighbours' edges, never on top of them. */
  const landingOf = useCallback(
    (d: Drag): Point | null => {
      if (!d.moved || isOverTray(d)) return null
      const pointer = toField(d.x, d.y)
      const wanted = { x: pointer.x - d.grab.x, y: pointer.y - d.grab.y }
      return settle(d.group, wanted, otherRects, SNAP_PX / (view.zoom * SCALE))
    },
    [isOverTray, toField, otherRects, view.zoom],
  )
  const landing = useMemo(() => (drag ? landingOf(drag) : null), [drag, landingOf])

  /** Every device where it stands right now, the carried one where it would land. */
  const standing = useMemo(() => {
    const list: { group: Group; at: Point }[] = []
    for (const group of placedGroups) {
      if (drag?.moved && drag.group.device.id === group.device.id) continue
      list.push({ group, at: placeOf.get(group.device.id)! })
    }
    if (drag?.moved && landing) list.push({ group: drag.group, at: landing })
    return list
  }, [placedGroups, placeOf, drag, landing])

  const seams = useMemo(
    () => seamsBetween(standing.map((s) => ({ id: s.group.device.id, rects: rectsAt(s.group, s.at) }))),
    [standing],
  )

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

      // Once it is a move it stays one: coming back towards where the press began must not put the card down.
      const moved = drag.moved || Math.hypot(point.x - drag.fromX, point.y - drag.fromY) > drag.threshold
      setDrag({ ...drag, x: point.x, y: point.y, moved })
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

    // Worked out from where the pointer is let go, not from the last frame drawn: a quick flick can end before
    // the field has drawn the move that started it.
    const up = toSurface(event.clientX, event.clientY)
    const moved = drag.moved || Math.hypot(up.x - drag.fromX, up.y - drag.fromY) > drag.threshold
    const final = { ...drag, x: up.x, y: up.y, moved }
    const landing = landingOf(final)
    const overTray = isOverTray(final)

    if (!moved) {
      setSelected(drag.group.device.id)
    } else if (pinch.current) {
      // The drag turned into a pinch: leave the device where it was.
    } else if (landing) {
      const id = drag.group.device.id
      setPlaces((current) => [...current.filter((p) => p.deviceId !== id), { deviceId: id, x: landing.x, y: landing.y }])
      setStatus(null)
    } else if (overTray && !drag.fromTray) {
      setPlaces((current) => current.filter((p) => p.deviceId !== drag.group.device.id))
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
    const point = toSurface(event.clientX, event.clientY)
    const pointer = toField(point.x, point.y)
    const at = placeOf.get(group.device.id)
    const b = group.bounds
    setSelected(group.device.id)
    setDrag({
      group,
      x: point.x,
      y: point.y,
      // Out of the tray it is carried by its middle; on the field, by the point that was pressed.
      grab:
        fromTray || !at
          ? { x: (b.left + b.right) / 2, y: (b.top + b.bottom) / 2 }
          : { x: pointer.x - at.x, y: pointer.y - at.y },
      fromX: point.x,
      fromY: point.y,
      threshold: dragStartsAfter(event.pointerType),
      moved: false,
      fromTray,
    })
    ;(surface.current as HTMLElement).setPointerCapture(event.pointerId)
  }

  const thisDevicePlaced = placedGroups.some((g) => g.device.isThisDevice)

  if (!hub) {
    return <div className="hub-loading">Загрузка…</div>
  }

  const dot = 24 * view.zoom

  return (
    <div className="hub">
      <header className="hub-head">
        <div className="hub-title">
          {/* The window's tabs name this page; here it is what is going on that is worth the room. */}
          <span className="hub-sub">{hub.inputFocus} · {hub.ownSound}</span>
        </div>
        <div className="hub-actions">
          <button className="btn btn-ghost btn-icon" onClick={() => zoomBy(1 / 1.25)} title="Отдалить">−</button>
          <button className="btn btn-ghost btn-icon" onClick={() => zoomBy(1.25)} title="Приблизить">+</button>
          <button className="btn btn-ghost" onClick={() => fitTo(places, groups)}>По центру</button>
          <button className="btn" onClick={() => setPlaces(saved)} disabled={!dirty || busy}>Отменить</button>
          <button className="btn btn-accent" onClick={() => void save()} disabled={!dirty || busy}>
            {busy ? 'Сохраняю…' : 'Сохранить раскладку'}
          </button>
        </div>
      </header>

      <div className="hub-body">
        <div
          ref={surface}
          className={`hub-field${drag?.moved ? ' is-dragging' : ''}`}
          onPointerDownCapture={onPointerDownCapture}
          onPointerDown={onSurfaceDown}
          onPointerMove={onSurfaceMove}
          onPointerUp={onSurfaceUp}
          onPointerCancel={onSurfaceUp}
          onWheel={onWheel}
        >
          <div
            className="hub-grid"
            style={{ backgroundSize: `${dot}px ${dot}px`, backgroundPosition: `${view.x}px ${view.y}px` }}
          />

          <div className="hub-world" style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.zoom})` }}>
            {standing.map(({ group, at }) => {
              const carried = drag?.moved === true && drag.group.device.id === group.device.id
              const others = standing.filter((s) => s.group !== group).flatMap((s) => rectsAt(s.group, s.at))
              return (
                <DeviceGroup
                  key={group.device.id}
                  group={group}
                  at={at}
                  selected={selected === group.device.id}
                  carried={carried}
                  lonely={standing.length > 1 && isAlone(rectsAt(group, at), others)}
                  onPointerDown={carried ? undefined : (e) => startDrag(group, e, false)}
                  onOpenScreen={(monitor) => void run('showScreen', { deviceId: group.device.id, monitor })}
                />
              )
            })}

            {seams.map((seam) => (
              <div
                key={seam.key}
                className={`hub-seam ${seam.vertical ? 'is-v' : 'is-h'}`}
                style={
                  seam.vertical
                    ? { left: seam.at * SCALE - 2, top: seam.from * SCALE, height: (seam.to - seam.from) * SCALE }
                    : { top: seam.at * SCALE - 2, left: seam.from * SCALE, width: (seam.to - seam.from) * SCALE }
                }
              />
            ))}
          </div>

          {/* Over the tray, the device follows the pointer as it is, to be let go of there. */}
          {drag?.moved && overTray && (
            <div
              className="hub-floating"
              style={{
                left: drag.x - (drag.grab.x - drag.group.bounds.left) * SCALE * view.zoom,
                top: drag.y - (drag.grab.y - drag.group.bounds.top) * SCALE * view.zoom,
                transform: `scale(${view.zoom})`,
              }}
            >
              <DeviceGroup group={drag.group} at={null} selected carried lonely={false} />
            </div>
          )}

          <Tray
            unplaced={unplaced}
            discovered={discovered}
            addresses={hub.manualAddresses}
            dragging={drag !== null && drag.moved && !drag.fromTray}
            onDragDevice={(group, event) => startDrag(groupById.get(group.device.id) ?? group, event, true)}
            onCommand={run}
          />
        </div>

        {selectedDevice && (
          <Inspector
            device={selectedDevice}
            placed={placeOf.has(selectedDevice.id)}
            onClose={() => setSelected(null)}
            onCommand={run}
            onRemove={() => setPlaces((current) => current.filter((p) => p.deviceId !== selectedDevice.id))}
          />
        )}
      </div>

      <footer className="hub-foot">
        {!thisDevicePlaced && <span className="hub-warn">Это устройство не на поле: его курсор никуда не перейдёт.</span>}
        {status && <span className="hub-status">{status}</span>}
        {dirty && !status && <span className="hub-status">Есть несохранённые изменения.</span>}
        {!status && !dirty && thisDevicePlaced && (
          <span className="hub-status">
            Курсор переходит там, где края мониторов соприкасаются, — эти места светятся. Сдвиньте устройство, чтобы
            сдвинуть границу.
          </span>
        )}
        <span className="hub-net">{hub.network}</span>
      </footer>
    </div>
  )
}

// ---- a device on the field --------------------------------------------------------------------------------

function DeviceGroup({
  group,
  at,
  selected,
  carried,
  lonely,
  onPointerDown,
  onOpenScreen,
}: {
  group: Group
  /** Where its origin stands on the field; null draws it at its own top-left (carried over the tray). */
  at: Point | null
  selected: boolean
  carried: boolean
  lonely: boolean
  onPointerDown?: (event: React.PointerEvent) => void
  onOpenScreen?: (monitor: string | null) => void
}) {
  const device = group.device
  const b = group.bounds
  const style: React.CSSProperties = {
    left: at ? (at.x + b.left) * SCALE : 0,
    top: at ? (at.y + b.top) * SCALE : 0,
    width: (b.right - b.left) * SCALE,
    height: (b.bottom - b.top) * SCALE,
  }

  return (
    <div
      className={`device${device.isThisDevice ? ' is-self' : ''}${device.online ? '' : ' is-offline'}${
        selected ? ' is-selected' : ''
      }${carried ? ' is-carried' : ''}`}
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
        <div
          key={pane.key ?? 'whole'}
          className="device-pane"
          style={{
            left: (pane.left - b.left) * SCALE,
            top: (pane.top - b.top) * SCALE,
            width: pane.width * SCALE,
            height: pane.height * SCALE,
          }}
        >
          <div className={`pane-screen${pane.primary ? ' is-primary' : ''}`}>
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

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max)
}

export type { Device }
