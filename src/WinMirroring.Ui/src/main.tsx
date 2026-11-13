import { StrictMode, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
// The tokens and the shared pieces come first, so a page's own stylesheet can refine them.
import './theme.css'
import { Hub } from './hub/Hub'
import { Panel } from './panel/Panel'
import { Settings } from './settings/Settings'

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
      return <Hub />
    case 'settings':
      return <Settings />
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
