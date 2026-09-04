import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

describe('desktop dependency contract', () => {
  it('builds the renderer with Tailwind CSS and daisyUI', () => {
    const packageJson = JSON.parse(readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'))
    const styles = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8')
    const app = readFileSync(resolve(process.cwd(), 'src/App.vue'), 'utf8')

    expect(packageJson.devDependencies.tailwindcss).toBe('4.3.3')
    expect(packageJson.devDependencies['@tailwindcss/vite']).toBe('4.3.3')
    expect(packageJson.devDependencies.daisyui).toBe('5.7.22')
    expect(packageJson.build.electronDist).toBe('node_modules/electron/dist')
    expect(styles).toContain('@import "tailwindcss";')
    expect(styles).toContain('@plugin "daisyui"')
    expect(app).toContain('h-screen min-h-0 overflow-hidden')
  })

  it('uses offline Iconify assets and does not include Lucide', () => {
    const packageJson = JSON.parse(readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'))
    expect(packageJson.dependencies['@iconify/vue']).toBeTruthy()
    expect(packageJson.dependencies['@iconify-icons/mdi']).toBeTruthy()
    expect(packageJson.dependencies['lucide-vue-next']).toBeUndefined()
    expect(packageJson.dependencies['@lucide/vue']).toBeUndefined()
  })

  it('keeps automation control behind the typed preload whitelist', () => {
    const preload = readFileSync(resolve(process.cwd(), 'electron/preload.cts'), 'utf8')
    expect(preload).toContain("ipcRenderer.invoke('automation:start')")
    expect(preload).toContain("ipcRenderer.invoke('automation:querySetup')")
    expect(preload).not.toContain('ipcRenderer: ipcRenderer')
  })
})
