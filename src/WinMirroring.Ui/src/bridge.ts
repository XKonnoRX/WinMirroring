/**
 * Talking to the app. Inside WinMirroring the page runs in WebView2 and messages go to C#; opened in an ordinary
 * browser (`npm run dev`) there is no host, so a small sample stands in and the interface can be worked on alone.
 */

type Reply = { id: number; ok: boolean; result?: unknown; error?: string }
type Push = { event: string; data?: unknown }

type Pending = { resolve: (value: unknown) => void; reject: (reason: Error) => void }

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage(message: unknown): void
        addEventListener(type: 'message', handler: (event: { data: unknown }) => void): void
      }
    }
  }
}

const host = window.chrome?.webview
const pending = new Map<number, Pending>()
const listeners = new Map<string, Set<(data: unknown) => void>>()
let nextId = 1

/** True while the page runs inside the app; false in a plain browser. */
export const inApp = host !== undefined

if (host) {
  host.addEventListener('message', (event) => {
    const message = event.data as Reply | Push
    if ('event' in message) {
      for (const handler of listeners.get(message.event) ?? []) {
        handler(message.data)
      }
      return
    }

    const waiter = pending.get(message.id)
    if (!waiter) return
    pending.delete(message.id)
    if (message.ok) waiter.resolve(message.result)
    else waiter.reject(new Error(message.error ?? 'не получилось'))
  })
}

/** Calls the app and waits for its answer. */
export function invoke<T>(method: string, args?: unknown): Promise<T> {
  if (!host) return mockInvoke<T>(method, args)

  const id = nextId++
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject })
    host.postMessage({ id, method, args: args ?? null })
  })
}

/** Subscribes to something the app pushes (state that changed). Returns the unsubscribe. */
export function on(event: string, handler: (data: unknown) => void): () => void {
  let set = listeners.get(event)
  if (!set) {
    set = new Set()
    listeners.set(event, set)
  }

  set.add(handler)
  return () => set.delete(handler)
}

// ---- standing in for the app, so the interface can be opened in a browser ----------------------------------

const device = (over: Record<string, unknown>) => ({
  isThisDevice: false,
  paired: true,
  online: true,
  state: 'на связи',
  address: '192.168.1.191:47621',
  rtt: '3 мс',
  fingerprint: 'AB:CD:EF:01:23:45',
  lastError: null,
  version: '1.0.14',
  canUpdate: false,
  monitors: [],
  hasVirtualDisplay: true,
  sound: { here: 'auto', playing: false, sentTo: false },
  ...over,
})

const sample = {
  self: 'self',
  inputFocus: 'Ввод: здесь',
  ownSound: 'Звук играет здесь',
  network: 'TCP: 192.168.1.156:47621; обнаружение: UDP 47620',
  manualAddresses: ['192.168.1.191'],
  devices: [
    device({
      id: 'self',
      name: 'KONNY',
      isThisDevice: true,
      state: 'это устройство',
      address: '',
      rtt: '',
      sound: null,
      monitors: [
        { key: 'self-1', label: 'Монитор 1 · основной', size: '1536×864', width: 1536, height: 864, left: 0, top: 0, primary: true },
        { key: 'self-2', label: 'Монитор 2 · слева', size: '1920×1080', width: 1920, height: 1080, left: -1920, top: 0, primary: false },
      ],
    }),
    device({
      id: 'tablet',
      name: 'DESKTOP-P50HIEF',
      canUpdate: true,
      version: '1.0.12',
      monitors: [{ key: 'tab-1', label: 'Монитор 1 · основной', size: '1920×1280', width: 1920, height: 1280, left: 0, top: 0, primary: true }],
    }),
    device({ id: 'pc', name: 'Домашний ПК', online: false, state: 'не на связи', version: null, hasVirtualDisplay: null }),
    device({ id: 'new', name: 'LAPTOP-X1', paired: false, state: 'найдено рядом', version: null, hasVirtualDisplay: null }),
  ],
  cells: [
    { deviceId: 'self', monitor: 'self-2', column: 0, row: 0 },
    { deviceId: 'self', monitor: 'self-1', column: 1, row: 0 },
    { deviceId: 'tablet', monitor: 'tab-1', column: 2, row: 0 },
  ],
}

async function mockInvoke<T>(method: string, args?: unknown): Promise<T> {
  if (method === 'hub.get') return sample as unknown as T
  if (method === 'hub.save') {
    sample.cells = (args as { cells: typeof sample.cells }).cells
    return 'Раскладка сохранена и отправлена устройствам.' as T
  }

  if (method === 'hub.command') {
    const call = args as { name: string; deviceId?: string; text?: string }
    if (call.name === 'addAddress' && call.text) sample.manualAddresses.push(call.text)
    if (call.name === 'removeAddress') sample.manualAddresses = sample.manualAddresses.filter((a) => a !== call.text)
    return `Команда «${call.name}» выполнена.` as T
  }

  throw new Error(`нет такого метода: ${method}`)
}
