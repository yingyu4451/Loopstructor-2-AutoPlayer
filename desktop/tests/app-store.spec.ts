import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAppStore } from '../src/stores/app'
import { useUiStore } from '../src/stores/ui'
import type { DesktopApi, HostSnapshot, SaveBackupEntry } from '../src/types'

function snapshot(autoplayActive = false): HostSnapshot {
  return {
    protocolVersion: 1,
    version: '0.6.68',
    settings: {
      gameRoot: '', profileName: 'Default', continueExistingProfile: false, gameMode: 'normal',
      overrideGameSpeed: false, speedState: 0, maxRunMinutes: 60, skipStory: false,
      decisionPriority: 0, uiScaleMode: 'system', customUiScalePercent: 100, characterCfgIndex: 0,
      activeRoute: 'game', sidebarCollapsed: false, skinId: 'skyspine', gitHubOwner: 'yingyu4451',
      gitHubRepository: 'Loopstructor-2-QA-Tool',
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

    await store.setRoute('settings')

    expect(store.route).toBe('settings')
    expect(saveSettings).toHaveBeenCalledOnce()
  })

  it('loads cheat catalogs silently when entering a cheat page', async () => {
    const saveSettings = vi.fn(async (settings) => settings)
    const cheatCommand = vi.fn(async (command: string) => command === 'cheat.queryCatalog'
      ? { success: true, message: '', data: { relics: [] } }
      : { success: true, message: '', data: { enabled: true } })
    window.loopstructorDesktop = { saveSettings, cheatCommand } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    await store.setRoute('relics')

    expect(cheatCommand.mock.calls.map(call => call[0])).toEqual(['cheat.queryCatalog', 'cheat.queryState'])
    expect(store.catalog).toEqual({ relics: [] })
    expect(store.cheatState).toEqual({ enabled: true })
    expect(useUiStore().activeToast).toBeUndefined()
  })

  it('loads cheat catalogs when a game connects while a cheat page is open', async () => {
    const cheatCommand = vi.fn(async (command: string) => command === 'cheat.queryCatalog'
      ? { success: true, message: '', data: { vehicles: [] } }
      : { success: true, message: '', data: { enabled: true } })
    window.loopstructorDesktop = { cheatCommand } as unknown as DesktopApi
    const store = useAppStore()
    const disconnected = snapshot()
    disconnected.settings.activeRoute = 'vehicles'
    disconnected.connection.trusted = false
    store.applySnapshot(disconnected)

    store.applySnapshot(snapshot())
    await vi.waitFor(() => expect(cheatCommand).toHaveBeenCalledTimes(2))

    expect(cheatCommand.mock.calls.map(call => call[0])).toEqual(['cheat.queryCatalog', 'cheat.queryState'])
    expect(useUiStore().activeToast).toBeUndefined()
  })

  it('keeps the selected route when a stale Host snapshot arrives', async () => {
    const saveSettings = vi.fn(async (settings) => settings)
    window.loopstructorDesktop = { saveSettings } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    await store.setRoute('settings')
    store.applySnapshot(snapshot())

    expect(store.route).toBe('settings')
  })

  it('does not navigate away when route persistence fails', async () => {
    const saveSettings = vi.fn(async () => { throw new Error('disk unavailable') })
    window.loopstructorDesktop = { saveSettings } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    await store.setRoute('saves')

    expect(store.route).toBe('saves')
  })

  it('preserves the renderer route when saving a stale settings draft', async () => {
    const saveSettings = vi.fn(async (settings) => settings)
    window.loopstructorDesktop = { saveSettings } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())
    await store.setRoute('settings')

    const staleDraft = { ...store.settings!, activeRoute: 'game' as const }
    await store.saveSettings(staleDraft, false)

    expect(store.route).toBe('settings')
    expect(saveSettings.mock.calls.at(-1)?.[0].activeRoute).toBe('settings')
    expect(store.settings?.activeRoute).toBe('settings')
  })

  it('persists the selected skin with the current renderer route', async () => {
    const saveSettings = vi.fn(async (settings) => settings)
    window.loopstructorDesktop = { saveSettings } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())
    await store.setRoute('settings')

    await store.saveSettings({ ...store.settings!, skinId: 'skyspine' }, false)

    expect(saveSettings.mock.calls.at(-1)?.[0].skinId).toBe('skyspine')
    expect(saveSettings.mock.calls.at(-1)?.[0].activeRoute).toBe('settings')
    expect(store.settings?.skinId).toBe('skyspine')
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

  it('confirms one selected backup before sending the restore request', async () => {
    const backup: SaveBackupEntry = {
      id: '第01章-第003关-20260901-120000', chapter: 1, level: 3,
      createdAt: '2026-09-01T12:00:00+08:00', fileCount: 4, totalBytes: 1024, isLatest: true,
    }
    const restoreSaveBackup = vi.fn(async () => ({
      success: true, backupId: backup.id, targetDirectory: 'C:\\Saves', gameRestarted: true,
      message: '读档完成', backups: [backup],
    }))
    window.loopstructorDesktop = { restoreSaveBackup } as unknown as DesktopApi
    const store = useAppStore()
    store.applySnapshot(snapshot())

    const pending = store.restoreSaveBackup(backup)
    expect(useUiStore().confirmDialog?.title).toBe('关闭游戏并读取存档')
    useUiStore().resolveConfirm(true)
    await pending

    expect(restoreSaveBackup).toHaveBeenCalledOnce()
    expect(restoreSaveBackup).toHaveBeenCalledWith(backup.id)
  })
})
