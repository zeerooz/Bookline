/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // The API runs on its own origin in development. Proxying keeps the
    // browser on a single origin, so no CORS preflight is needed locally.
    proxy: {
      '/api': {
        target: process.env.VITE_API_PROXY ?? 'http://localhost:5090',
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
})
