import { fileURLToPath, URL } from 'node:url'
import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

const backend = process.env.MAILNIX_BACKEND_URL ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    // The SPA talks to the API same-origin; in development Vite forwards to the backend.
    proxy: {
      '/api': backend,
      '/hubs': { target: backend, ws: true },
      '/healthz': backend,
    },
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.spec.ts'],
  },
})
