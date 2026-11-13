/** What the side panel shows. */

export type PanelView = {
  streamId: number
  title: string
  peerId: string
  kind: string
  active: boolean
  inputAllowed: boolean
  inputOn: boolean
}

export type PanelDevice = {
  id: string
  name: string
  monitors: { index: number; label: string; primary: boolean; key: string }[]
  screenOpen: boolean
  secondMonitor: boolean
  canBecome: boolean | null
  sound: { here: string; playing: boolean; sentTo: boolean }
}

export type PanelState = {
  localActive: boolean
  ownSound: string
  inputFocus: string
  views: PanelView[]
  bar: { visible: boolean; active: string; presets: string[] }
  input: { enabled: boolean; syncKeyboardLayout: boolean; adminControl: boolean }
  devices: PanelDevice[]
}
