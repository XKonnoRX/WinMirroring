import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { LayoutCanvas } from './layout/LayoutCanvas'
import './theme.css'

/** Which screen to show is in the address: the app opens the page at "…/index.html#layout". */
function Screen() {
  const route = window.location.hash.replace('#', '') || 'layout'
  switch (route) {
    case 'layout':
      return <LayoutCanvas />
    default:
      return <div style={{ padding: 24 }}>Нет такой страницы: {route}</div>
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Screen />
  </StrictMode>,
)
