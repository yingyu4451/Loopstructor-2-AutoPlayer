import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

describe('desktop dependency contract', () => {
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
