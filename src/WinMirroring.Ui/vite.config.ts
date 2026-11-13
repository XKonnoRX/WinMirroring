import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The app serves these files from a folder next to the executable, so every path must be relative.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'chrome120',
    chunkSizeWarningLimit: 1500,
  },
  server: { host: '127.0.0.1', port: 5183, strictPort: true },
})
