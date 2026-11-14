import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
// The tokens and the shared pieces come first, so a page's own stylesheet can refine them.
import './theme.css'
import { on } from './bridge'
import { Hub } from './hub/Hub'
import { Panel } from './panel/Panel'
import { Settings } from './settings/Settings'

type Tab = 'devices' | 'settings'

/**
 * The main window: the devices and the settings as two tabs of one page rather than two windows. The settings
 * used to open a window of their own, which took a moment to load every time and was easy to leave open
 * somewhere behind everything else. Both tabs stay mounted — switching one only shows what is already there, so
 * it is instant and each keeps where it was scrolled to.
 */
function Main() {
  const [tab, setTab] = useState<Tab>('devices')

  // "Параметры" elsewhere in the app (the side panel, the tray) brings this window up on that tab.
  useEffect(() => on('hub.openSettings', () => setTab('settings')), [])

  return (
    <div className="shell">
      <nav className="shell-tabs">
        {([
          ['devices', 'Устройства'],
          ['settings', 'Параметры'],
        ] as const).map(([key, label]) => (
          <button
            key={key}
            className={`btn${tab === key ? ' btn-accent' : ' btn-ghost'}`}
            onClick={() => setTab(key)}
          >
            {label}
          </button>
        ))}
      </nav>
      <div className="shell-page" hidden={tab !== 'devices'}>
        <Hub />
      </div>
      <div className="shell-page" hidden={tab !== 'settings'}>
        <Settings />
      </div>
    </div>
  )
}

/** Which screen to show is in the address: the app opens the page at "…/index.html#hub". */
function Screen() {
  const [route, setRoute] = useState(window.location.hash.replace('#', '') || 'hub')
  useEffect(() => {
    const follow = () => setRoute(window.location.hash.replace('#', '') || 'hub')
    window.addEventListener('hashchange', follow)
    return () => window.removeEventListener('hashchange', follow)
  }, [])

  switch (route) {
    case 'hub':
    case 'layout':
    case 'settings':
      return <Main />
    case 'panel':
      return <Panel />
    default:
      return <div style={{ padding: 24 }}>Нет такой страницы: {route}</div>
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Screen />
  </StrictMode>,
)
