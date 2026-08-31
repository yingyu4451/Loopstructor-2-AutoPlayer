import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAppStore } from '../src/stores/app'
import { useUiStore } from '../src/stores/ui'
import type { DesktopApi, HostSnapshot } from '../src/types'

function snapshot(autoplayActive = false): HostSnapshot {
  return {
    protocolVersion: 1,
    version: '0.6.52',
    settings: {
      gameRoot: '', profileName: 'Default', continueExistingProfile: false, gameMode: 'normal',
      overrideGameSpeed: false, speedState: 0, maxRunMinutes: 60, skipStory: false,
      decisionPriority: 0, uiScaleMode: 'system', customUiScalePercent: 100, characterCfgIndex: 0,
      activeRoute: 'game', sidebarCollapsed: false, gitHubOwner: 'yingyu4451',
      gitHubRepository: 'Loopstructor-2-AutoPlayer',
    },
    connection: {
      trusted: true, label: '已连接游戏', reason: '', cheatAvailable: true, autoplayActive,
    },
    logs: [],
  }
}

describe('desktop application store', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('blocks mutation RPCs while an old automation session is active', async () => {
    const cheatCommand = vi.fn()
    window.loopstructorDesktop = { cheatCommand } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot(true))

    const result = await store.command('cheat.grantVehicle', { vehicleId: 'Shell_L1' })

    expect(result).toBeUndefined()
    expect(cheatCommand).not.toHaveBeenCalled()
    expect(useUiStore().activeToast?.kind).toBe('warning')
  })

  it('persists route changes through the typed preload API', async () => {
    const saveSettings = vi.fn(async (settings) => settings)
    window.loopstructorDesktop = { saveSettings } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    await store.setRoute('relics')

    expect(store.route).toBe('relics')
    expect(saveSettings).toHaveBeenCalledOnce()
  })

  it('keeps the selected route when a stale Host snapshot arrives', async () => {
    const saveSettings = vi.fn(async (settings) => settings)
    window.loopstructorDesktop = { saveSettings } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    await store.setRoute('vehicles')
    store.applySnapshot(snapshot())

    expect(store.route).toBe('vehicles')
  })

  it('does not navigate away when route persistence fails', async () => {
    const saveSettings = vi.fn(async () => { throw new Error('disk unavailable') })
    window.loopstructorDesktop = { saveSettings } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    await store.setRoute('battle')

    expect(store.route).toBe('battle')
  })

  it('exposes the restored automation controls through the preload API', async () => {
    const startAutomation = vi.fn(async () => ({ success: true, message: 'started' }))
    window.loopstructorDesktop = { startAutomation } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    const response = await store.startAutomation()

    expect(response?.success).toBe(true)
    expect(startAutomation).toHaveBeenCalledOnce()
  })
})
