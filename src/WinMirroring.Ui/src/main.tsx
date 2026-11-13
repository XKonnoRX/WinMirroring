import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// The tokens and the shared pieces come first, so a page's own stylesheet can refine them.
import './theme.css'
import { Hub } from './hub/Hub'

/** Which screen to show is in the address: the app opens the page at "…/index.html#hub". */
function Screen() {
  const route = window.location.hash.replace('#', '') || 'hub'
  switch (route) {
    case 'hub':
    case 'layout':
      return <Hub />
    default:
      return <div style={{ padding: 24 }}>Нет такой страницы: {route}</div>
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Screen />
  </StrictMode>,
)
