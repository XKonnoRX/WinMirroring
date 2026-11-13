/** The layout as the app keeps it: devices with their monitors, and which cell of the grid each one sits in. */

export type Monitor = {
  key: string
  label: string
  size: string
  width: number
  height: number
  primary: boolean
}

export type Device = {
  id: string
  name: string
  isThisDevice: boolean
  online: boolean
  monitors: Monitor[]
}

/** One square of the grid. A cell without a monitor stands for the device as a whole. */
export type Cell = {
  deviceId: string
  monitor: string | null
  column: number
  row: number
}

export type LayoutState = { devices: Device[]; cells: Cell[] }

/**
 * Something that can be put on the field: a device as a whole, or one monitor of a device that has several.
 * A device whose monitors are unknown (offline, or an older build) is placed as a whole.
 */
export type Piece = {
  id: string
  deviceId: string
  monitor: string | null
  device: Device
  title: string
  subtitle: string
  size: string
  aspect: number
  primary: boolean
}

export function piecesOf(devices: Device[]): Piece[] {
  const pieces: Piece[] = []
  for (const device of devices) {
    if (device.monitors.length > 1) {
      for (const monitor of device.monitors) {
        pieces.push({
          id: `${device.id}|${monitor.key}`,
          deviceId: device.id,
          monitor: monitor.key,
          device,
          title: device.name,
          subtitle: monitor.label,
          size: monitor.size,
          aspect: aspectOf(monitor),
          primary: monitor.primary,
        })
      }
      continue
    }

    const only = device.monitors[0]
    pieces.push({
      id: `${device.id}|`,
      deviceId: device.id,
      monitor: null,
      device,
      title: device.name,
      subtitle: only ? only.label : 'Устройство целиком',
      size: only?.size ?? '',
      aspect: only ? aspectOf(only) : 16 / 9,
      primary: only?.primary ?? true,
    })
  }

  return pieces
}

function aspectOf(monitor: Monitor): number {
  return monitor.width > 0 && monitor.height > 0 ? monitor.width / monitor.height : 16 / 9
}

export function cellKey(column: number, row: number): string {
  return `${column},${row}`
}

export function pieceIdOf(cell: Cell): string {
  return `${cell.deviceId}|${cell.monitor ?? ''}`
}

/** Cells of the four neighbours, in the order the cursor would cross them. */
export const neighbourOffsets = [
  { dc: -1, dr: 0 },
  { dc: 1, dr: 0 },
  { dc: 0, dr: -1 },
  { dc: 0, dr: 1 },
] as const
