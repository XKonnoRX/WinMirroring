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
  shownMonitor: null,
  introducedBy: null,
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
        { key: 'self-2', label: 'Монитор 2 · основной', size: '1920×1080', width: 1920, height: 1080, left: 0, top: 0, primary: true },
        { key: 'self-3', label: 'Монитор 3 · слева', size: '1080×1920', width: 1080, height: 1920, left: -1080, top: -394, primary: false },
        { key: 'self-1', label: 'Монитор 1 · справа', size: '1920×1080', width: 1920, height: 1080, left: 1920, top: 104, primary: false },
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
    device({
      id: 'studio',
      name: 'STUDIO-PC',
      paired: false,
      online: false,
      state: 'известно через DESKTOP-P50HIEF',
      version: null,
      hasVirtualDisplay: null,
      introducedBy: 'DESKTOP-P50HIEF',
    }),
  ],
  cells: [
    { deviceId: 'self', monitor: 'self-3', column: 0, row: 0 },
    { deviceId: 'self', monitor: 'self-2', column: 1, row: 1 },
    { deviceId: 'self', monitor: 'self-1', column: 2, row: 2 },
    { deviceId: 'tablet', monitor: 'tab-1', column: 3, row: 2 },
  ],
  placements: [] as { deviceId: string; x: number; y: number }[],
}

const sampleSettings = {
  device: {
    name: 'KONNY',
    id: '6f780cba46ba408fa8078e6ee0cfb38d',
    fingerprint: 'A8:CD:EF:01:23:45:67:89',
    network: 'TCP: 192.168.1.156:47621; обнаружение: UDP 47620',
    version: '1.0.16',
    versionText: 'Версия 1.0.16, последняя.',
    status: '',
  },
  input: {
    enabled: true,
    edgeResistance: 8,
    smoothing: true,
    maxSmoothingDelay: 30,
    syncKeyboardLayout: true,
    smoothingText: '',
    adminControl: false,
    adminControlText: 'Отдельный помощник вводит мышь и клавиатуру в окна администратора.',
    adminHelper: true,
    adminNoPrompt: false,
    elevationText: 'WinMirroring запущен с обычными правами.',
    canRestartElevated: true,
  },
  clipboard: { enabled: true, shareFiles: true, dragDrop: true, maxInline: 64, directPaste: 64, activity: '18:02 Получено с DESKTOP-P50HIEF: текст' },
  audio: {
    send: true,
    followInput: true,
    pinnedId: '',
    muteLocal: true,
    receive: true,
    bitrate: 160,
    buffer: 40,
    status: 'Звук этого устройства играет здесь.',
    targets: [{ id: '', name: '—' }, { id: 'tablet', name: 'DESKTOP-P50HIEF' }],
  },
  screen: {
    allowViewing: true,
    allowControl: true,
    windowDrag: true,
    hideSentWindow: true,
    autoKeyboard: false,
    keyboardButton: true,
    directPresent: true,
    swipeMonitors: true,
    showStats: false,
    closeUnusedMinutes: 5,
    maxFps: 60,
    quality: 70,
    codec: 'Auto',
    codecs: [
      { value: 'Auto', label: 'Авто — H.264, иначе JPEG' },
      { value: 'H264', label: 'H.264 (аппаратный)' },
      { value: 'Jpeg', label: 'JPEG (изменённые участки)' },
    ],
    videoBitrate: 12000,
    virtualDisplay: 'Драйвер виртуального монитора установлен.',
    virtualDisplayInstalled: true,
    sources: [{ streamId: 1, title: 'Монитор 1 (1536×864)', peer: 'DESKTOP-P50HIEF' }],
  },
  shell: { allowCommands: false, allowElevatedCommands: false },
  power: {
    saving: 'OnBattery',
    fps: 30,
    savingNow: false,
    onBattery: false,
    options: [
      { value: 'Never', label: 'Никогда' },
      { value: 'OnBattery', label: 'Когда устройство на батарее' },
      { value: 'Always', label: 'Всегда' },
    ],
  },
  network: {
    discoveryPort: 47620,
    transportPort: 47621,
    interfaces: [
      { id: 'wifi', name: 'Wi-Fi', address: '192.168.1.156', details: '192.168.1.0/24', selected: true, vpn: false },
      { id: 'tun', name: 'throne-tun', address: '172.19.0.1', details: '172.19.0.0/24', selected: false, vpn: true },
    ],
    addresses: ['192.168.1.191'],
  },
  startup: { withWindows: true, autoUpdate: true },
  health: [
    { name: 'Общий ввод', ok: true, detail: 'хуки установлены' },
    { name: 'Второй монитор', ok: false, detail: 'не установлен драйвер' },
  ],
}

const samplePanel = {
  localActive: false,
  ownSound: 'Сейчас играет здесь',
  inputFocus: 'Ввод: здесь',
  views: [
    { streamId: 1, title: 'DESKTOP-P50HIEF: Монитор 1', peerId: 'tablet', kind: 'monitor', active: true, inputAllowed: true, inputOn: true, zoom: 1 },
    { streamId: 2, title: 'KONNY: Блокнот', peerId: 'self', kind: 'window', active: false, inputAllowed: true, inputOn: true, zoom: 1 },
  ],
  stats: false,
  bar: { visible: true, active: 'Основные', presets: ['Основные', 'Браузер', 'Текст'] },
  input: { enabled: true, syncKeyboardLayout: true, adminControl: false },
  devices: [
    {
      id: 'tablet',
      name: 'DESKTOP-P50HIEF',
      monitors: [{ index: 0, label: '1920×1280', primary: true, key: '\\\\.\\DISPLAY1' }],
      screenOpen: true,
      secondMonitor: false,
      canBecome: true,
      sound: { here: 'Automatic', playing: false, sentTo: false },
    },
  ],
}

async function mockInvoke<T>(method: string, args?: unknown): Promise<T> {
  if (method === 'settings.get') return sampleSettings as unknown as T
  if (method === 'panel.get') return samplePanel as unknown as T
  if (method === 'settings.set' || method === 'settings.command' || method === 'panel.command') {
    return null as T
  }

  if (method === 'hub.get') return sample as unknown as T
  if (method === 'hub.save') {
    sample.placements = (args as { placements: typeof sample.placements }).placements
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

/**
 * A way to stand in for the app from the browser's own console, and only where there is no app: `wm.invoke` to
 * change what the sample holds, `wm.push` to say that something changed, exactly as the app does through
 * WebView2. Without it a page opened in a browser can only ever be loaded afresh, so anything the app pushes —
 * a layout another device changed, for one — could not be tried out at all without building the whole app.
 */
if (!host) {
  Object.assign(window, {
    wm: {
      invoke,
      push: (event: string, data?: unknown) => {
        for (const handler of listeners.get(event) ?? []) {
          handler(data)
        }
      },
    },
  })
}
