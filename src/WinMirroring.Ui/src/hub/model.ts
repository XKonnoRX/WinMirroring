/** What the app tells the page about itself, the other devices and where they stand. */

export type Monitor = {
  key: string
  label: string
  size: string
  width: number
  height: number
  left: number
  top: number
  primary: boolean
}

export type Sound = { here: 'auto' | 'always' | 'never'; playing: boolean; sentTo: boolean }

export type Device = {
  id: string
  name: string
  isThisDevice: boolean
  paired: boolean
  online: boolean
  state: string
  address: string
  /** The address without a port: empty when this device has not been seen on the network yet. */
  host: string
  rtt: string
  fingerprint: string
  lastError: string | null
  version: string | null
  canUpdate: boolean
  monitors: Monitor[]
  hasVirtualDisplay: boolean | null
  sound: Sound | null
  /** The monitor of this device that is open here right now, if any. */
  shownMonitor: string | null
  /**
   * Name of the paired device that vouched for this one. Such a device can be trusted with one press instead of a
   * confirmation on both screens, because the device that vouched was confirmed by hand already.
   */
  introducedBy: string | null
  /** Not connected now, and known well enough to be woken over the local network (its adapter's address was seen). */
  canWake: boolean
}

/** One square of the old grid. A cell without a monitor stands for the whole device. */
export type Cell = { deviceId: string; monitor: string | null; column: number; row: number }

/** Where a device stands on the field: where its desktop's origin (its primary monitor's top-left) lies, in pixels. */
export type Placement = { deviceId: string; x: number; y: number }

export type Hub = {
  self: string
  inputFocus: string
  ownSound: string
  network: string
  manualAddresses: string[]
  devices: Device[]
  cells: Cell[]
  placements: Placement[]
}

/** One monitor of a device, in that device's own desktop pixels. */
export type Pane = {
  key: string | null
  label: string
  size: string
  primary: boolean
  left: number
  top: number
  width: number
  height: number
}

export type Rect = { left: number; top: number; right: number; bottom: number }

/** A device on the field: its monitors move together, standing as they stand on its own desktop. */
export type Group = {
  device: Device
  panes: Pane[]
  /** The box around all its monitors, in its desktop pixels. */
  bounds: Rect
}

export type Point = { x: number; y: number }

/** The size a device is drawn at when it has not said what its monitors are. */
const DEFAULT_W = 1920
const DEFAULT_H = 1080

/** How far apart two edges may be and still meet — the same allowance the app makes when the cursor crosses. */
export const EDGE_TOLERANCE = 48

export function groupOf(device: Device): Group {
  const known = device.monitors.filter((m) => m.width > 0 && m.height > 0)
  const panes: Pane[] =
    known.length > 0
      ? known.map((m) => ({
          key: m.key,
          label: m.label,
          size: m.size,
          primary: m.primary,
          left: m.left,
          top: m.top,
          width: m.width,
          height: m.height,
        }))
      : [{ key: null, label: 'Экран', size: '', primary: true, left: 0, top: 0, width: DEFAULT_W, height: DEFAULT_H }]

  return { device, panes, bounds: unionOf(panes.map(paneRect)) }
}

export function paneRect(pane: Pane): Rect {
  return { left: pane.left, top: pane.top, right: pane.left + pane.width, bottom: pane.top + pane.height }
}

/** The monitors of a device standing at `at`, in field pixels. */
export function rectsAt(group: Group, at: Point): Rect[] {
  return group.panes.map((pane) => shift(paneRect(pane), at))
}

export function shift(rect: Rect, by: Point): Rect {
  return { left: rect.left + by.x, top: rect.top + by.y, right: rect.right + by.x, bottom: rect.bottom + by.y }
}

export function unionOf(rects: Rect[]): Rect {
  return rects.reduce(
    (a, b) => ({
      left: Math.min(a.left, b.left),
      top: Math.min(a.top, b.top),
      right: Math.max(a.right, b.right),
      bottom: Math.max(a.bottom, b.bottom),
    }),
    { left: Infinity, top: Infinity, right: -Infinity, bottom: -Infinity },
  )
}

const overlap = (a0: number, a1: number, b0: number, b1: number) => Math.min(a1, b1) - Math.max(a0, b0)

function intersects(a: Rect, b: Rect) {
  return overlap(a.left, a.right, b.left, b.right) > 0 && overlap(a.top, a.bottom, b.top, b.bottom) > 0
}

/**
 * Where a device being carried to `wanted` comes to rest: its edges drawn onto the edges of the monitors around it
 * when they are close (within `snap` field pixels), as Windows does with monitors, and then pushed out of any
 * monitor it would cover, along the shorter way. Two devices may touch, never overlap.
 */
export function settle(moving: Group, wanted: Point, others: Rect[], snap: number): Point {
  let at = { ...wanted }
  const mine = rectsAt(moving, at)

  // Snap each axis on its own, to the nearest candidate: edge to the opposite edge (they meet), or edge to the same
  // edge (they line up).
  let bestX: number | null = null
  let bestY: number | null = null
  for (const r of mine) {
    for (const o of others) {
      const closeY = overlap(r.top, r.bottom, o.top, o.bottom) > -snap
      const closeX = overlap(r.left, r.right, o.left, o.right) > -snap
      if (closeY) {
        for (const d of [o.right - r.left, o.left - r.right, o.left - r.left, o.right - r.right]) {
          if (Math.abs(d) <= snap && (bestX === null || Math.abs(d) < Math.abs(bestX))) bestX = d
        }
      }
      if (closeX) {
        for (const d of [o.bottom - r.top, o.top - r.bottom, o.top - r.top, o.bottom - r.bottom]) {
          if (Math.abs(d) <= snap && (bestY === null || Math.abs(d) < Math.abs(bestY))) bestY = d
        }
      }
    }
  }
  at = { x: at.x + (bestX ?? 0), y: at.y + (bestY ?? 0) }

  // Then out of whatever it still covers.
  for (let pass = 0; pass < 8; pass++) {
    let moved = false
    for (const r of rectsAt(moving, at)) {
      for (const o of others) {
        if (!intersects(r, o)) continue
        const push = [
          { x: o.right - r.left, y: 0 },
          { x: o.left - r.right, y: 0 },
          { x: 0, y: o.bottom - r.top },
          { x: 0, y: o.top - r.bottom },
        ].sort((a, b) => Math.abs(a.x) + Math.abs(a.y) - (Math.abs(b.x) + Math.abs(b.y)))[0]!
        at = { x: at.x + push.x, y: at.y + push.y }
        moved = true
      }
    }
    if (!moved) break
  }

  return { x: Math.round(at.x), y: Math.round(at.y) }
}

/** A stretch of edge the cursor crosses through, in field pixels; `vertical` for a side edge. */
export type Seam = { key: string; vertical: boolean; at: number; from: number; to: number }

/**
 * Every place the cursor goes from one device to another: where an edge of one device's monitor meets an edge of
 * another's, along the part they share. This is the same rule the app crosses by, so what glows is what works.
 */
export function seamsBetween(placed: { id: string; rects: Rect[] }[]): Seam[] {
  const seams: Seam[] = []
  for (let i = 0; i < placed.length; i++) {
    for (let j = i + 1; j < placed.length; j++) {
      for (const a of placed[i]!.rects) {
        for (const b of placed[j]!.rects) {
          const [left, right] = a.left <= b.left ? [a, b] : [b, a]
          if (Math.abs(right.left - left.right) <= EDGE_TOLERANCE) {
            const from = Math.max(a.top, b.top)
            const to = Math.min(a.bottom, b.bottom)
            if (to > from) {
              seams.push({ key: `v${i}.${j}.${seams.length}`, vertical: true, at: (left.right + right.left) / 2, from, to })
            }
          }
          const [upper, lower] = a.top <= b.top ? [a, b] : [b, a]
          if (Math.abs(lower.top - upper.bottom) <= EDGE_TOLERANCE) {
            const from = Math.max(a.left, b.left)
            const to = Math.min(a.right, b.right)
            if (to > from) {
              seams.push({ key: `h${i}.${j}.${seams.length}`, vertical: false, at: (upper.bottom + lower.top) / 2, from, to })
            }
          }
        }
      }
    }
  }
  return seams
}

/**
 * Positions for a layout saved before devices could stand freely, worked out from its grid. The grid only says
 * which square is next to which, so that is what is kept: starting from one device (this one, if it is there),
 * every device that was next to a placed one on the grid is put against that monitor's edge, centred on it.
 * Devices the grid did not connect to anything are stood to the right. Saved from here, it becomes a free layout.
 */
export function placementsFromCells(groups: Group[], cells: Cell[]): Placement[] {
  type Spot = { group: Group; rect: Rect }
  const at = new Map<string, Spot>()
  const byDevice = new Map<string, { column: number; row: number; rect: Rect }[]>()
  for (const group of groups) {
    for (const cell of cells.filter((c) => c.deviceId === group.device.id)) {
      const pane = group.panes.find((p) => p.key === (cell.monitor ?? null))
      const rect = pane ? paneRect(pane) : group.bounds
      at.set(`${cell.column},${cell.row}`, { group, rect })
      const list = byDevice.get(group.device.id) ?? []
      list.push({ column: cell.column, row: cell.row, rect })
      byDevice.set(group.device.id, list)
    }
  }

  const placed = new Map<string, Point>()
  const order = groups
    .filter((g) => byDevice.has(g.device.id))
    .sort((a, b) => Number(b.device.isThisDevice) - Number(a.device.isThisDevice))

  const steps = [
    { dc: 1, dr: 0 },
    { dc: -1, dr: 0 },
    { dc: 0, dr: 1 },
    { dc: 0, dr: -1 },
  ]

  for (const root of order) {
    if (placed.has(root.device.id)) continue

    // A part the grid did not join to what is placed already: it stands to the right of all of it.
    const sofar = [...placed.entries()].flatMap(([id, p]) => rectsAt(groups.find((g) => g.device.id === id)!, p))
    const right = sofar.length > 0 ? unionOf(sofar).right + 400 - root.bounds.left : 0
    placed.set(root.device.id, { x: right, y: 0 })

    const queue = [root.device.id]
    while (queue.length > 0) {
      const id = queue.shift()!
      const origin = placed.get(id)!
      for (const cell of byDevice.get(id) ?? []) {
        const a = shift(cell.rect, origin)
        for (const step of steps) {
          const next = at.get(`${cell.column + step.dc},${cell.row + step.dr}`)
          if (!next || placed.has(next.group.device.id)) continue
          const b = next.rect
          const midA = { x: (a.left + a.right) / 2, y: (a.top + a.bottom) / 2 }
          const midB = { x: (b.left + b.right) / 2, y: (b.top + b.bottom) / 2 }
          const x = step.dc > 0 ? a.right - b.left : step.dc < 0 ? a.left - b.right : midA.x - midB.x
          const y = step.dr > 0 ? a.bottom - b.top : step.dr < 0 ? a.top - b.bottom : midA.y - midB.y
          placed.set(next.group.device.id, { x: Math.round(x), y: Math.round(y) })
          queue.push(next.group.device.id)
        }
      }
    }
  }

  return [...placed.entries()].map(([deviceId, p]) => ({ deviceId, x: p.x, y: p.y }))
}

/** The same devices standing at the same places, in whatever order. */
export function samePlacements(a: Placement[], b: Placement[]): boolean {
  if (a.length !== b.length) return false
  const key = (p: Placement) => `${p.deviceId}@${p.x},${p.y}`
  const left = a.map(key).sort()
  const right = b.map(key).sort()
  return left.every((value, index) => value === right[index])
}

/** True when no monitor of this device meets a monitor of another one: the cursor cannot get to it or from it. */
export function isAlone(mine: Rect[], others: Rect[]): boolean {
  return !mine.some((a) =>
    others.some((b) => {
      const sideBySide =
        (Math.abs(b.left - a.right) <= EDGE_TOLERANCE || Math.abs(a.left - b.right) <= EDGE_TOLERANCE) &&
        overlap(a.top, a.bottom, b.top, b.bottom) > 0
      const stacked =
        (Math.abs(b.top - a.bottom) <= EDGE_TOLERANCE || Math.abs(a.top - b.bottom) <= EDGE_TOLERANCE) &&
        overlap(a.left, a.right, b.left, b.right) > 0
      return sideBySide || stacked
    }),
  )
}
