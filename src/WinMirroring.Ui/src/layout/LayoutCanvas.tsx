import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { invoke, on } from '../bridge'
import { Cell, Device, LayoutState, Piece, cellKey, neighbourOffsets, pieceIdOf, piecesOf } from './model'
import './LayoutCanvas.css'

/** Distance between the centres of two neighbouring cells, and the size of the card drawn in one. */
const PITCH_X = 260
const PITCH_Y = 180
const CARD_W = 228
const CARD_H = 150

const MIN_ZOOM = 0.35
const MAX_ZOOM = 2

type View = { x: number; y: number; zoom: number }

type Drag = {
  piece: Piece
  /** Where the cursor is, in the surface's pixels. */
  x: number
  y: number
  /** Where inside the card it was grabbed, so the card does not jump under the finger. */
  grabX: number
  grabY: number
  moved: boolean
  fromTray: boolean
}

export function LayoutCanvas() {
  const surface = useRef<HTMLDivElement>(null)
  const [devices, setDevices] = useState<Device[]>([])
  const [cells, setCells] = useState<Cell[]>([])
  const [saved, setSaved] = useState<Cell[]>([])
  const [view, setView] = useState<View>({ x: 0, y: 0, zoom: 1 })
  const [drag, setDrag] = useState<Drag | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const pieces = useMemo(() => piecesOf(devices), [devices])
  const placed = useMemo(() => {
    const map = new Map<string, Cell>()
    for (const cell of cells) map.set(pieceIdOf(cell), cell)
    return map
  }, [cells])
  const occupied = useMemo(() => {
    const map = new Map<string, Piece>()
    for (const cell of cells) {
      const piece = pieces.find((p) => p.id === pieceIdOf(cell))
      if (piece) map.set(cellKey(cell.column, cell.row), piece)
    }
    return map
  }, [cells, pieces])

  const tray = useMemo(() => pieces.filter((p) => !placed.has(p.id)), [pieces, placed])
  const dirty = useMemo(() => !sameCells(cells, saved), [cells, saved])

  // ---- loading ------------------------------------------------------------------------------------------

  const load = useCallback(async (recentre: boolean) => {
    const state = await invoke<LayoutState>('layout.get')
    setDevices(state.devices)
    setCells(state.cells)
    setSaved(state.cells)
    if (recentre) fitTo(state.cells)
  }, [])

  const fitTo = useCallback((list: Cell[]) => {
    const box = surface.current?.getBoundingClientRect()
    if (!box) return

    if (list.length === 0) {
      setView({ x: box.width / 2, y: box.height / 2, zoom: 1 })
      return
    }

    const columns = list.map((c) => c.column)
    const rows = list.map((c) => c.row)
    const left = Math.min(...columns) * PITCH_X - CARD_W / 2
    const right = Math.max(...columns) * PITCH_X + CARD_W / 2
    const top = Math.min(...rows) * PITCH_Y - CARD_H / 2
    const bottom = Math.max(...rows) * PITCH_Y + CARD_H / 2
    const margin = 90
    const zoom = clamp(
      Math.min((box.width - margin * 2) / (right - left), (box.height - margin * 2) / (bottom - top), 1.15),
      MIN_ZOOM,
      MAX_ZOOM,
    )

    setView({
      x: box.width / 2 - ((left + right) / 2) * zoom,
      y: box.height / 2 - ((top + bottom) / 2) * zoom,
      zoom,
    })
  }, [])

  useEffect(() => {
    void load(true)
    return on('layout.changed', () => void load(false))
  }, [load])

  // ---- the field ----------------------------------------------------------------------------------------

  const toWorld = useCallback(
    (clientX: number, clientY: number) => {
      const box = surface.current!.getBoundingClientRect()
      return {
        x: (clientX - box.left - view.x) / view.zoom,
        y: (clientY - box.top - view.y) / view.zoom,
      }
    },
    [view],
  )

  const toSurface = useCallback((clientX: number, clientY: number) => {
    const box = surface.current!.getBoundingClientRect()
    return { x: clientX - box.left, y: clientY - box.top }
  }, [])

  const hoveredCell = useMemo(() => {
    if (!drag || !surface.current) return null
    const box = surface.current.getBoundingClientRect()
    const centreX = drag.x - drag.grabX + CARD_W / 2
    const centreY = drag.y - drag.grabY + CARD_H / 2
    if (centreY > box.height - TRAY_H) return null

    const world = {
      x: (centreX - view.x) / view.zoom,
      y: (centreY - view.y) / view.zoom,
    }
    return { column: Math.round(world.x / PITCH_X), row: Math.round(world.y / PITCH_Y) }
  }, [drag, view])

  const panning = useRef<{ x: number; y: number; view: View } | null>(null)

  const onSurfaceDown = (event: React.PointerEvent) => {
    if (event.button !== 0 && event.button !== 1) return
    panning.current = { x: event.clientX, y: event.clientY, view }
    ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  }

  const onSurfaceMove = (event: React.PointerEvent) => {
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
    panning.current = null
    if (!drag) return

    if (drag.moved && hoveredCell) {
      place(drag.piece, hoveredCell.column, hoveredCell.row)
    } else if (drag.moved && !drag.fromTray) {
      remove(drag.piece)
    }

    setDrag(null)
    try {
      ;(event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId)
    } catch {
      // the pointer is already gone
    }
  }

  const onWheel = (event: React.WheelEvent) => {
    const world = toWorld(event.clientX, event.clientY)
    const zoom = clamp(view.zoom * (event.deltaY < 0 ? 1.12 : 1 / 1.12), MIN_ZOOM, MAX_ZOOM)
    const point = toSurface(event.clientX, event.clientY)
    setView({ x: point.x - world.x * zoom, y: point.y - world.y * zoom, zoom })
  }

  const zoomBy = (factor: number) => {
    const box = surface.current?.getBoundingClientRect()
    if (!box) return
    const zoom = clamp(view.zoom * factor, MIN_ZOOM, MAX_ZOOM)
    const centre = { x: box.width / 2, y: box.height / 2 }
    setView({
      x: centre.x - ((centre.x - view.x) / view.zoom) * zoom,
      y: centre.y - ((centre.y - view.y) / view.zoom) * zoom,
      zoom,
    })
  }

  // ---- moving the pieces --------------------------------------------------------------------------------

  const startDrag = (piece: Piece, event: React.PointerEvent, fromTray: boolean) => {
    event.stopPropagation()
    const card = (event.currentTarget as HTMLElement).getBoundingClientRect()
    const point = toSurface(event.clientX, event.clientY)
    setDrag({
      piece,
      x: point.x,
      y: point.y,
      grabX: fromTray ? CARD_W / 2 : event.clientX - card.left,
      grabY: fromTray ? CARD_H / 2 : event.clientY - card.top,
      moved: false,
      fromTray,
    })
    ;(surface.current as HTMLElement).setPointerCapture(event.pointerId)
  }

  const place = (piece: Piece, column: number, row: number) => {
    setCells((current) => {
      const mine = current.find((c) => pieceIdOf(c) === piece.id)
      const taken = current.find((c) => c.column === column && c.row === row && pieceIdOf(c) !== piece.id)
      const next = current.filter((c) => pieceIdOf(c) !== piece.id && c !== taken)
      next.push({ deviceId: piece.deviceId, monitor: piece.monitor, column, row })

      // Dropped onto an occupied cell: the two change places, so nothing is lost by accident.
      if (taken) {
        if (mine) next.push({ ...taken, column: mine.column, row: mine.row })
        else next.push(taken)
      }

      return next
    })
    setStatus(null)
  }

  const remove = (piece: Piece) => {
    setCells((current) => current.filter((c) => pieceIdOf(c) !== piece.id))
    setStatus(null)
  }

  // ---- saving -------------------------------------------------------------------------------------------

  const save = async () => {
    setBusy(true)
    try {
      await invoke('layout.save', { cells })
      setSaved(cells)
      setStatus('Раскладка сохранена и отправлена устройствам.')
    } catch (error) {
      setStatus(error instanceof Error ? error.message : 'Не удалось сохранить.')
    } finally {
      setBusy(false)
    }
  }

  const thisDevicePlaced = cells.some((c) => devices.find((d) => d.id === c.deviceId)?.isThisDevice)

  return (
    <div className="layout-page">
      <header className="layout-head">
        <div>
          <h1>Раскладка экранов</h1>
          <p>
            Расставьте устройства так, как их экраны стоят на столе. Курсор переходит через край к соседу по сетке.
            Устройства можно ставить и отдельными группами.
          </p>
        </div>
        <div className="layout-actions">
          <button className="btn btn-ghost btn-icon" onClick={() => zoomBy(1 / 1.25)} title="Отдалить">
            −
          </button>
          <button className="btn btn-ghost btn-icon" onClick={() => zoomBy(1.25)} title="Приблизить">
            +
          </button>
          <button className="btn btn-ghost" onClick={() => fitTo(cells)}>
            По центру
          </button>
          <button className="btn" onClick={() => setCells(saved)} disabled={!dirty || busy}>
            Отменить
          </button>
          <button className="btn btn-accent" onClick={() => void save()} disabled={!dirty || busy}>
            {busy ? 'Сохраняю…' : 'Сохранить'}
          </button>
        </div>
      </header>

      <div
        ref={surface}
        className={`layout-surface${drag ? ' is-dragging' : ''}`}
        onPointerDown={onSurfaceDown}
        onPointerMove={onSurfaceMove}
        onPointerUp={onSurfaceUp}
        onPointerCancel={onSurfaceUp}
        onWheel={onWheel}
      >
        <div
          className="layout-grid"
          style={{
            backgroundSize: `${PITCH_X * view.zoom}px ${PITCH_Y * view.zoom}px`,
            backgroundPosition: `${view.x - (PITCH_X / 2) * view.zoom}px ${view.y - (PITCH_Y / 2) * view.zoom}px`,
          }}
        />

        <div className="layout-world" style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.zoom})` }}>
          {hoveredCell && (
            <div
              className={`layout-slot${occupied.has(cellKey(hoveredCell.column, hoveredCell.row)) ? ' is-swap' : ''}`}
              style={rectOf(hoveredCell.column, hoveredCell.row)}
            />
          )}

          {seamsOf(cells, occupied).map((seam) => (
            <div key={seam.key} className={`layout-seam ${seam.horizontal ? 'is-h' : 'is-v'}`} style={seam.style} />
          ))}

          {cells.map((cell) => {
            const piece = pieces.find((p) => p.id === pieceIdOf(cell))
            if (!piece || drag?.piece.id === piece.id) return null
            const lonely = !neighbourOffsets.some((o) => occupied.has(cellKey(cell.column + o.dc, cell.row + o.dr)))
            return (
              <div key={piece.id} className="layout-placed" style={rectOf(cell.column, cell.row)}>
                <PieceCard piece={piece} lonely={lonely} onPointerDown={(e) => startDrag(piece, e, false)} />
              </div>
            )
          })}
        </div>

        {drag && (
          <div
            className="layout-floating"
            style={{ left: drag.x - drag.grabX, top: drag.y - drag.grabY, width: CARD_W, height: CARD_H }}
          >
            <PieceCard piece={drag.piece} lonely={false} />
          </div>
        )}

        <footer
          className={`layout-tray${drag && !drag.fromTray ? ' is-target' : ''}${
            tray.length === 0 && !drag ? ' is-empty' : ''
          }`}
        >
          <div className="layout-tray-title">
            {tray.length > 0 ? 'Не на поле' : 'Все устройства расставлены'}
            <span className="layout-tray-hint">
              {drag && !drag.fromTray
                ? 'Отпустите здесь, чтобы убрать с поля'
                : tray.length === 0
                  ? 'Перетащите карточку сюда, чтобы убрать её с поля'
                  : 'Перетащите на поле'}
            </span>
          </div>
          <div className="layout-tray-items">
            {tray.map((piece) => (
              <div key={piece.id} className="layout-tray-item">
                <PieceCard piece={piece} lonely={false} compact onPointerDown={(e) => startDrag(piece, e, true)} />
              </div>
            ))}
          </div>
        </footer>
      </div>

      <div className="layout-foot">
        {!thisDevicePlaced && cells.length > 0 && (
          <span className="layout-warn">Это устройство не на поле: его курсор никуда не перейдёт.</span>
        )}
        {status && <span className="layout-status">{status}</span>}
        {dirty && !status && <span className="layout-status">Есть несохранённые изменения.</span>}
      </div>
    </div>
  )
}

// ---- pieces ---------------------------------------------------------------------------------------------

function PieceCard({
  piece,
  lonely,
  compact,
  onPointerDown,
}: {
  piece: Piece
  lonely: boolean
  compact?: boolean
  onPointerDown?: (event: React.PointerEvent) => void
}) {
  const screen = {
    // The monitor's own proportions, so a tall tablet does not look like a wide screen.
    aspectRatio: String(piece.aspect),
  }

  return (
    <div
      className={`piece${piece.device.isThisDevice ? ' is-self' : ''}${piece.device.online ? '' : ' is-offline'}${
        compact ? ' is-compact' : ''
      }`}
      onPointerDown={onPointerDown}
    >
      <div className="piece-screen" style={screen}>
        <div className="piece-glow" />
        <div className="piece-name">{piece.title}</div>
        {piece.size && <div className="piece-size">{piece.size}</div>}
      </div>
      <div className="piece-foot">
        <span className="piece-label">{piece.subtitle}</span>
        {piece.device.isThisDevice && <span className="chip chip-accent">здесь</span>}
        {!piece.device.online && <span className="chip">не на связи</span>}
        {lonely && <span className="chip">отдельно</span>}
      </div>
    </div>
  )
}

// ---- geometry -------------------------------------------------------------------------------------------

const TRAY_H = 150

function rectOf(column: number, row: number) {
  return {
    left: column * PITCH_X - CARD_W / 2,
    top: row * PITCH_Y - CARD_H / 2,
    width: CARD_W,
    height: CARD_H,
  }
}

/** The seams between neighbouring cells: where the cursor crosses from one screen to the next. */
function seamsOf(cells: Cell[], occupied: Map<string, Piece>) {
  const seams: { key: string; horizontal: boolean; style: React.CSSProperties }[] = []
  for (const cell of cells) {
    if (occupied.has(cellKey(cell.column + 1, cell.row))) {
      seams.push({
        key: `h${cell.column},${cell.row}`,
        horizontal: true,
        style: {
          left: cell.column * PITCH_X + CARD_W / 2,
          top: cell.row * PITCH_Y - 18,
          width: PITCH_X - CARD_W,
        },
      })
    }

    if (occupied.has(cellKey(cell.column, cell.row + 1))) {
      seams.push({
        key: `v${cell.column},${cell.row}`,
        horizontal: false,
        style: {
          left: cell.column * PITCH_X - 18,
          top: cell.row * PITCH_Y + CARD_H / 2,
          height: PITCH_Y - CARD_H,
        },
      })
    }
  }

  return seams
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max)
}

function sameCells(a: Cell[], b: Cell[]) {
  if (a.length !== b.length) return false
  const key = (c: Cell) => `${pieceIdOf(c)}@${c.column},${c.row}`
  const left = a.map(key).sort()
  const right = b.map(key).sort()
  return left.every((value, index) => value === right[index])
}
