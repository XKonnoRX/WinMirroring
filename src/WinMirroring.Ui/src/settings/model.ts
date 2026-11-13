/** Everything the settings page shows, as the app hands it over. */

export type SettingsState = {
  device: { name: string; id: string; fingerprint: string; network: string; version: string; versionText: string; status: string }
  input: {
    enabled: boolean
    edgeResistance: number
    smoothing: boolean
    maxSmoothingDelay: number
    syncKeyboardLayout: boolean
    smoothingText: string
    adminControl: boolean
    adminControlText: string
    elevationText: string
    canRestartElevated: boolean
  }
  clipboard: { enabled: boolean; shareFiles: boolean; dragDrop: boolean; maxInline: number; directPaste: number; activity: string }
  audio: {
    send: boolean
    followInput: boolean
    pinnedId: string
    muteLocal: boolean
    receive: boolean
    bitrate: number
    buffer: number
    status: string
    targets: { id: string; name: string }[]
  }
  screen: {
    allowViewing: boolean
    allowControl: boolean
    windowDrag: boolean
    hideSentWindow: boolean
    closeUnusedMinutes: number
    maxFps: number
    quality: number
    codec: string
    codecs: { value: string; label: string }[]
    videoBitrate: number
    virtualDisplay: string
    sources: { streamId: number; title: string; peer: string }[]
  }
  network: {
    discoveryPort: number
    transportPort: number
    interfaces: { id: string; name: string; address: string; details: string; selected: boolean; vpn: boolean }[]
    addresses: string[]
  }
  startup: { withWindows: boolean; autoUpdate: boolean }
  health: { name: string; ok: boolean; detail: string }[]
}
