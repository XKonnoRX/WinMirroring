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
}

/** One square of the grid the cursor crosses between. A cell without a monitor stands for the whole device. */
export type Cell = { deviceId: string; monitor: string | null; column: number; row: number }

export type Hub = {
  self: string
  inputFocus: string
  ownSound: string
  network: string
  manualAddresses: string[]
  devices: Device[]
  cells: Cell[]
}

/** One monitor inside its device, at a fixed place relative to the others. */
export type Pane = { key: string | null; label: string; size: string; aspect: number; primary: boolean; dc: number; dr: number }

/** A device on the field: its monitors move together, and neighbours may attach to any of them. */
export type Group = {
  device: Device
  panes: Pane[]
  columns: number
  rows: number
}

/**
 * The monitors of a device, placed relative to each other the way they really are on that desktop: the distinct
 * left edges become columns and the distinct top edges rows. A device that has not told us its monitors (it is
 * offline, or runs an older build) is one pane, which the layout treats as the device as a whole.
 */
export function groupOf(device: Device): Group {
  if (device.monitors.length === 0) {
    return {
      device,
      panes: [{ key: null, label: 'Экран', size: '', aspect: 16 / 9, primary: true, dc: 0, dr: 0 }],
      columns: 1,
      rows: 1,
    }
  }

  if (device.monitors.length === 1) {
    const only = device.monitors[0]!
    return { device, panes: [paneOf(only, 0, 0)], columns: 1, rows: 1 }
  }

  const unknown = device.monitors.every((m) => m.left === 0 && m.top === 0)
  if (unknown) {
    // No positions: lay them out in a row, primary first, which is the order they are numbered in.
    const panes = device.monitors.map((m, index) => paneOf(m, index, 0))
    return { device, panes, columns: panes.length, rows: 1 }
  }

  const columns = ranks(device.monitors.map((m) => m.left))
  const rows = ranks(device.monitors.map((m) => m.top))
  const panes = device.monitors.map((m) => paneOf(m, columns.get(m.left) ?? 0, rows.get(m.top) ?? 0))
  return { device, panes, columns: columns.size, rows: rows.size }
}

function paneOf(monitor: Monitor, dc: number, dr: number): Pane {
  return {
    key: monitor.key,
    label: monitor.label,
    size: monitor.size,
    aspect: monitor.width > 0 && monitor.height > 0 ? monitor.width / monitor.height : 16 / 9,
    primary: monitor.primary,
    dc,
    dr,
  }
}

function ranks(values: number[]): Map<number, number> {
  const sorted = [...new Set(values)].sort((a, b) => a - b)
  return new Map(sorted.map((value, index) => [value, index]))
}

/** Where a device stands: the cell its top-left monitor occupies, or null when it is not on the field. */
export function anchorOf(group: Group, cells: Cell[]): { column: number; row: number } | null {
  const mine = cells.filter((c) => c.deviceId === group.device.id)
  if (mine.length === 0) return null

  for (const pane of group.panes) {
    const cell = mine.find((c) => (c.monitor ?? null) === pane.key)
    if (cell) return { column: cell.column - pane.dc, row: cell.row - pane.dr }
  }

  // The device's monitors changed since the layout was saved: keep the place, re-derive the rest.
  const first = mine[0]!
  return { column: first.column, row: first.row }
}

/** The cells a device would occupy standing at this anchor. */
export function cellsOf(group: Group, anchor: { column: number; row: number }): Cell[] {
  return group.panes.map((pane) => ({
    deviceId: group.device.id,
    monitor: pane.key,
    column: anchor.column + pane.dc,
    row: anchor.row + pane.dr,
  }))
}

export function cellKey(column: number, row: number): string {
  return `${column},${row}`
}

export const neighbourOffsets = [
  { dc: -1, dr: 0 },
  { dc: 1, dr: 0 },
  { dc: 0, dr: -1 },
  { dc: 0, dr: 1 },
] as const

export function sameCells(a: Cell[], b: Cell[]): boolean {
  if (a.length !== b.length) return false
  const key = (c: Cell) => `${c.deviceId}|${c.monitor ?? ''}@${c.column},${c.row}`
  const left = a.map(key).sort()
  const right = b.map(key).sort()
  return left.every((value, index) => value === right[index])
}
