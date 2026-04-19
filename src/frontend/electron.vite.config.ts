import { defineConfig, externalizeDepsPlugin } from 'electron-vite'
import vue from '@vitejs/plugin-vue'
// Tailwind CSS removed — pure CSS design tokens + Element Plus components
import { resolve } from 'path'
import { readFileSync } from 'node:fs'

const pkg = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf-8')) as { version: string }

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()],
    build: {
      outDir: 'dist-electron',
      rollupOptions: {
        input: resolve(__dirname, 'electron/main.ts')
      }
    }
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    build: {
      outDir: 'dist-electron',
      emptyOutDir: false,
      rollupOptions: {
        input: resolve(__dirname, 'electron/preload.ts')
      }
    }
  },
  renderer: {
    root: resolve(__dirname, 'src'),
    server: {
      // 5173 collides with sibling LiberShare dev on same workstation — use 5190 for wdc
      port: 5190,
      strictPort: false,
      host: '127.0.0.1',
    },
    define: {
      'import.meta.env.VITE_APP_VERSION': JSON.stringify(pkg.version),
    },
    build: {
      // Keep the renderer output alongside main/preload under dist-electron/
      // so main.ts can resolve '../renderer/index.html' relative to its own
      // __dirname in the packaged app.asar. The default electron-vite layout
      // drops renderer into `out/renderer/`, which sits at a different path
      // in the packaged tree and produced a blank-window production build.
      outDir: 'dist-electron/renderer',
      emptyOutDir: true,
      rollupOptions: {
        input: resolve(__dirname, 'src/index.html')
      }
    },
    plugins: [vue()]
  }
})
